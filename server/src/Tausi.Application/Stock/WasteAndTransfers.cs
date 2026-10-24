using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Events;
using Tausi.Application.Ledger;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Printing;
using Tausi.Domain.Stock;

namespace Tausi.Application.Stock;

public sealed record RecordWasteRequest(Guid LocationId, Guid StockItemId, decimal Quantity, string? Unit, string Reason, string? Note);

public sealed record TransferLineRequest(Guid StockItemId, decimal Quantity, string? Unit);

public sealed record TransferRequest(Guid FromLocationId, Guid ToLocationId, List<TransferLineRequest>? Lines, Guid? PrinterId);

/// <summary>
/// One line of the Stock activity tab (waste and transfers) or of the Awaiting approval list.
/// <c>Kind</c>: Waste, Transfer, Delivery, Reversal or Count.
/// </summary>
public sealed record StockActivityDto(
    Guid Id, string Kind, DateTimeOffset At, string Summary, string Location, decimal? Value, string Status, string By,
    string? ApprovedBy, string? Note, string ApproveKey);

/// <summary>
/// Waste (stock.waste) goes out at the average cost to Stock loss; a transfer (stock.receive) moves
/// stock between locations at the average cost, so the books don't change. Either waits as Pending
/// when the key needs approval.
/// </summary>
public sealed class WasteAndTransferCommands(
    ITausiDb db, ICurrentSession session, StockAccess access, StockBook book, ILedgerPostingService ledger, PrintQueue queue,
    BusinessClock businessClock, IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public const string WasteSource = "Waste";

    public async Task<StockActivityDto> RecordWaste(RecordWasteRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        var postNow = await access.PostsNow(user, PermissionKeys.StockWaste, ct);
        var item = await db.StockItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == r.StockItemId, ct) ?? throw new DomainException("Choose the stock item.");
        if (!await db.StockLocations.AnyAsync(l => l.Id == r.LocationId, ct)) throw new DomainException("Choose the location.");
        if (r.Quantity <= 0) throw new DomainException("Enter how much was wasted.");
        var reason = WasteReasons.All.FirstOrDefault(x => string.Equals(x, r.Reason?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainException("Choose a reason.");
        var note = r.Note?.Trim();
        if (reason == WasteReasons.Other && string.IsNullOrEmpty(note)) throw new DomainException("Add a note for Other.", "note_required");
        if (note is { Length: > WasteRecord.MaxNoteLength }) throw new DomainException("Keep the note short.");
        var unit = string.IsNullOrWhiteSpace(r.Unit) ? item.BaseUnitName : r.Unit.Trim();
        var waste = new WasteRecord
        {
            LocationId = r.LocationId, StockItemId = item.Id, Quantity = r.Quantity, Unit = unit, BaseQuantity = r.Quantity * item.FactorOf(unit),
            Reason = reason, Note = string.IsNullOrEmpty(note) ? null : note, Status = StockDocumentStatus.Pending,
            CreatedByUserId = user.UserId, CreatedAt = clock.GetUtcNow(),
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.WasteRecords.Add(waste);
        audit.Write(new AuditRecord(AuditActions.WasteRecorded, nameof(WasteRecord), waste.Id.ToString(), null,
            new { item = item.Name, waste.Quantity, waste.Unit, waste.Reason, waste.Note, pending = !postNow }));
        if (postNow) await PostWaste(waste, user.UserId, null, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await StockEvents.Publish(events, "waste", ct);
        return (await Activity(null, null, null, ct, [waste.Id])).Single();
    }

    public async Task<StockActivityDto> RecordTransfer(TransferRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        var postNow = await access.PostsNow(user, PermissionKeys.StockReceive, ct);
        if (r.FromLocationId == r.ToLocationId) throw new DomainException("Choose two different locations.");
        if (await db.StockLocations.CountAsync(l => l.Id == r.FromLocationId || l.Id == r.ToLocationId, ct) != 2)
            throw new DomainException("Choose where it comes from and where it goes.");
        var lines = r.Lines ?? [];
        if (lines.Count == 0) throw new DomainException("Add at least one item.");
        var ids = lines.Select(l => l.StockItemId).Distinct().ToList();
        var items = await db.StockItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var transfer = new StockTransfer
        {
            FromLocationId = r.FromLocationId, ToLocationId = r.ToLocationId, Status = StockDocumentStatus.Pending,
            CreatedByUserId = user.UserId, CreatedAt = clock.GetUtcNow(),
        };
        foreach (var line in lines)
        {
            if (!items.TryGetValue(line.StockItemId, out var item)) throw new DomainException("That stock item no longer exists.");
            if (line.Quantity <= 0) throw new DomainException($"Enter how much {item.Name} moves.");
            var unit = string.IsNullOrWhiteSpace(line.Unit) ? item.BaseUnitName : line.Unit.Trim();
            transfer.Lines.Add(new StockTransferLine
            {
                TransferId = transfer.Id, StockItemId = item.Id, Quantity = line.Quantity, Unit = unit, BaseQuantity = line.Quantity * item.FactorOf(unit),
            });
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.StockTransfers.Add(transfer);
        audit.Write(new AuditRecord(AuditActions.TransferRecorded, nameof(StockTransfer), transfer.Id.ToString(), null,
            new { from = r.FromLocationId, to = r.ToLocationId, lines = transfer.Lines.Select(l => new { l.StockItemId, l.Quantity, l.Unit }), pending = !postNow }));
        if (postNow) await PostTransfer(transfer, user.UserId, null, ct);
        if (r.PrinterId is { } printerId) await PrintSlip(transfer, printerId, items, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await StockEvents.Publish(events, "transfers", ct);
        return (await Activity(null, null, null, ct, [transfer.Id])).Single();
    }

    public async Task<StockActivityDto> Approve(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.WasteRecords.FirstOrDefaultAsync(w => w.Id == id, ct) is { } waste)
        {
            if (waste.Status != StockDocumentStatus.Pending) throw new ConflictException("This isn't waiting for approval.", StockCodes.NotPending);
            await access.RequireApprover(user, PermissionKeys.StockWaste, waste.CreatedByUserId, ct);
            await PostWaste(waste, user.UserId, user.UserId, ct);
        }
        else
        {
            var transfer = await db.StockTransfers.Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Not found.");
            if (transfer.Status != StockDocumentStatus.Pending) throw new ConflictException("This isn't waiting for approval.", StockCodes.NotPending);
            await access.RequireApprover(user, PermissionKeys.StockReceive, transfer.CreatedByUserId, ct);
            await PostTransfer(transfer, user.UserId, user.UserId, ct);
        }
        audit.Write(new AuditRecord(AuditActions.StockDocumentApproved, "StockDocument", id.ToString(), new { status = "Pending" }, new { status = "Posted" }, user.UserId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await StockEvents.Publish(events, "activity", ct);
        return (await Activity(null, null, null, ct, [id])).Single();
    }

    public async Task<StockActivityDto> Reject(Guid id, RejectStockRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        var note = r.Note?.Trim();
        if (await db.WasteRecords.FirstOrDefaultAsync(w => w.Id == id, ct) is { } waste)
        {
            if (waste.Status != StockDocumentStatus.Pending) throw new ConflictException("This isn't waiting for approval.", StockCodes.NotPending);
            await access.RequireApprover(user, PermissionKeys.StockWaste, waste.CreatedByUserId, ct);
            waste.Status = StockDocumentStatus.Rejected;
            waste.RejectNote = note;
        }
        else
        {
            var transfer = await db.StockTransfers.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Not found.");
            if (transfer.Status != StockDocumentStatus.Pending) throw new ConflictException("This isn't waiting for approval.", StockCodes.NotPending);
            await access.RequireApprover(user, PermissionKeys.StockReceive, transfer.CreatedByUserId, ct);
            transfer.Status = StockDocumentStatus.Rejected;
            transfer.RejectNote = note;
        }
        audit.Write(new AuditRecord(AuditActions.StockDocumentRejected, "StockDocument", id.ToString(), null, new { note }, user.UserId));
        await db.SaveChangesAsync(ct);
        await StockEvents.Publish(events, "activity", ct);
        return (await Activity(null, null, null, ct, [id])).Single();
    }

    private async Task PostWaste(WasteRecord waste, Guid userId, Guid? approvedBy, CancellationToken ct)
    {
        var day = await WorkPeriods.Current(db, ct);
        var cost = await db.StockItems.Where(i => i.Id == waste.StockItemId).Select(i => i.AverageCost).FirstAsync(ct);
        var moved = await book.Move(waste.StockItemId, waste.LocationId, -waste.BaseQuantity, cost, StockMovementKind.Waste,
            StockReferences.Waste, waste.Id, userId, day?.Id, ct);
        waste.Cost = MoneyMath.Round(-moved.Value);
        waste.Status = StockDocumentStatus.Posted;
        waste.ApprovedByUserId = approvedBy;
        waste.PostedAt = clock.GetUtcNow();
        if (waste.Cost > 0)
            await ledger.Post(new JournalEntryDraft(clock.GetUtcNow(), $"Waste · {waste.Reason}", WasteSource, waste.Id.ToString(),
                [JournalLineDraft.Dr(LedgerAccountCodes.StockLoss, waste.Cost), JournalLineDraft.Cr(LedgerAccountCodes.Stock, waste.Cost)],
                day?.Id), ct);
    }

    private async Task PostTransfer(StockTransfer transfer, Guid userId, Guid? approvedBy, CancellationToken ct)
    {
        var day = await WorkPeriods.Current(db, ct);
        var ids = transfer.Lines.Select(l => l.StockItemId).ToList();
        var costs = await db.StockItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.AverageCost, ct);
        foreach (var line in transfer.Lines)
        {
            await book.Move(line.StockItemId, transfer.FromLocationId, -line.BaseQuantity, costs[line.StockItemId], StockMovementKind.TransferOut,
                StockReferences.Transfer, transfer.Id, userId, day?.Id, ct);
            await book.Move(line.StockItemId, transfer.ToLocationId, line.BaseQuantity, costs[line.StockItemId], StockMovementKind.TransferIn,
                StockReferences.Transfer, transfer.Id, userId, day?.Id, ct);
        }
        transfer.Status = StockDocumentStatus.Posted;
        transfer.ApprovedByUserId = approvedBy;
        transfer.PostedAt = clock.GetUtcNow();
    }

    /// <summary>A transfer slip on the chosen printer: from, to and the lines in their units. Never undoes the transfer.</summary>
    private async Task PrintSlip(StockTransfer transfer, Guid printerId, Dictionary<Guid, StockItem> items, CancellationToken ct)
    {
        var printer = await db.Printers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == printerId && p.Active, ct);
        if (printer is null) return;
        var locations = await db.StockLocations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var by = await db.Users.Where(u => u.Id == transfer.CreatedByUserId).Select(u => u.Name).FirstAsync(ct);
        var doc = StockSlips.Transfer(locations[transfer.FromLocationId], locations[transfer.ToLocationId], await businessClock.Now(ct), by,
            transfer.Lines.Select(l => (items[l.StockItemId].Name, $"{l.Quantity:0.##} {l.Unit}")).ToList(),
            transfer.Status == StockDocumentStatus.Pending, new SlipStyle(printer.Columns, printer.CutAfterSlip, null));
        queue.Enqueue(printer, PrintJobKind.Stock, doc, null);
    }

    /// <summary>Waste and transfers (newest first), optionally only Pending ones, or these ids.</summary>
    public async Task<IReadOnlyList<StockActivityDto>> Activity(
        string? kind, DateOnly? from, StockDocumentStatus? status, CancellationToken ct, IReadOnlyCollection<Guid>? only = null)
    {
        if (only is null) await access.RequireAny(session.RequireUser(), ct);
        var locations = await db.StockLocations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var items = await db.StockItems.AsNoTracking().ToDictionaryAsync(i => i.Id, ct);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var zone = Domain.Settings.BusinessTimeZone.Find((await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessTimeZoneKey));
        DateTimeOffset? since = from is { } f ? StockCatalog.Start(f, zone) : null;
        var result = new List<StockActivityDto>();

        if (kind is null or "Waste")
        {
            var q = db.WasteRecords.AsNoTracking().AsQueryable();
            if (only is not null) q = q.Where(w => only.Contains(w.Id));
            if (since is { } s) q = q.Where(w => w.CreatedAt >= s);
            if (status is { } st) q = q.Where(w => w.Status == st);
            foreach (var w in await q.OrderByDescending(w => w.CreatedAt).Take(300).ToListAsync(ct))
            {
                var item = items[w.StockItemId];
                result.Add(new StockActivityDto(w.Id, "Waste", w.CreatedAt,
                    $"{item.Name} · {w.Quantity:0.##} {w.Unit} · {w.Reason}", locations.GetValueOrDefault(w.LocationId, "?"),
                    w.Status == StockDocumentStatus.Posted ? w.Cost : MoneyMath.Round(w.BaseQuantity * item.AverageCost), w.Status.ToString(),
                    users.GetValueOrDefault(w.CreatedByUserId, "?"), w.ApprovedByUserId is { } a ? users.GetValueOrDefault(a) : null,
                    w.Note ?? w.RejectNote, PermissionKeys.StockWaste));
            }
        }
        if (kind is null or "Transfer")
        {
            var q = db.StockTransfers.AsNoTracking().Include(t => t.Lines).AsQueryable();
            if (only is not null) q = q.Where(t => only.Contains(t.Id));
            if (since is { } s) q = q.Where(t => t.CreatedAt >= s);
            if (status is { } st) q = q.Where(t => t.Status == st);
            foreach (var t in await q.OrderByDescending(t => t.CreatedAt).Take(300).ToListAsync(ct))
                result.Add(new StockActivityDto(t.Id, "Transfer", t.CreatedAt,
                    string.Join(", ", t.Lines.Select(l => $"{items[l.StockItemId].Name} {l.Quantity:0.##} {l.Unit}")),
                    $"{locations.GetValueOrDefault(t.FromLocationId, "?")} → {locations.GetValueOrDefault(t.ToLocationId, "?")}", null, t.Status.ToString(),
                    users.GetValueOrDefault(t.CreatedByUserId, "?"), t.ApprovedByUserId is { } a ? users.GetValueOrDefault(a) : null,
                    t.RejectNote, PermissionKeys.StockReceive));
        }
        return result.OrderByDescending(r => r.At).ToList();
    }
}

/// <summary>Everything waiting for someone to approve: pending deliveries, reversals, waste and transfers, and submitted counts.</summary>
public sealed class AwaitingApproval(ITausiDb db, ICurrentSession session, StockAccess access, WasteAndTransferCommands activity)
{
    public async Task<IReadOnlyList<StockActivityDto>> Execute(CancellationToken ct)
    {
        await access.RequireAny(session.RequireUser(), ct);
        var result = (await activity.Activity(null, null, StockDocumentStatus.Pending, ct)).ToList();
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var locations = await db.StockLocations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        foreach (var d in await db.Deliveries.AsNoTracking()
                     .Where(d => d.Status == StockDocumentStatus.Pending || d.ReversalStatus == StockDocumentStatus.Pending).ToListAsync(ct))
        {
            var reversal = d.Status == StockDocumentStatus.Posted;
            result.Add(new StockActivityDto(d.Id, reversal ? "Reversal" : "Delivery", reversal ? d.ReversalRequestedAt ?? d.CreatedAt : d.CreatedAt,
                $"{suppliers.GetValueOrDefault(d.SupplierId, "?")} · invoice {d.InvoiceNumber}", locations.GetValueOrDefault(d.LocationId, "?"),
                d.Total, "Pending", users.GetValueOrDefault(reversal ? d.ReversalRequestedByUserId ?? d.CreatedByUserId : d.CreatedByUserId, "?"),
                null, reversal ? d.ReversalReason : null, reversal ? PermissionKeys.StockAdjust : PermissionKeys.StockReceive));
        }
        foreach (var c in await db.StockCounts.AsNoTracking().Where(c => c.Status == StockCountStatus.Submitted).ToListAsync(ct))
            result.Add(new StockActivityDto(c.Id, "Count", c.SubmittedAt ?? c.AsAt,
                c.IsOpening ? "Opening stock" : c.Kind == StockCountKind.Full ? "Full count" : "Partial count",
                locations.GetValueOrDefault(c.LocationId, "?"), c.VarianceValue, "Pending", users.GetValueOrDefault(c.CountedByUserId, "?"), null, null,
                PermissionKeys.StockAdjust));
        return result.OrderBy(r => r.At).ToList();
    }
}
