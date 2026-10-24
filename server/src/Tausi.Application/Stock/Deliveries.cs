using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Events;
using Tausi.Application.Ledger;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Settings;
using Tausi.Domain.Stock;

namespace Tausi.Application.Stock;

public sealed record SupplierDto(Guid Id, string Name, string Phone, string KraPin, string Notes, bool Active, decimal Owed, long RowVersion);

public sealed record SaveSupplierRequest(string Name, string? Phone, string? KraPin, string? Notes, bool Active, long? RowVersion);

public sealed record DeliveryLineRequest(Guid StockItemId, string? Unit, decimal Quantity, decimal UnitCost);

public sealed record ReceiveDeliveryRequest(
    Guid SupplierId, string InvoiceNumber, DateOnly? Date, Guid LocationId, DeliveryPayment Payment, string? PaymentReference,
    List<DeliveryLineRequest>? Lines);

public sealed record DeliveryLineDto(Guid StockItemId, string StockItem, string Unit, decimal Factor, decimal Quantity, decimal UnitCost, decimal LineTotal);

public sealed record DeliveryDto(
    Guid Id, DateOnly Date, Guid SupplierId, string Supplier, string InvoiceNumber, Guid LocationId, string Location, decimal Total,
    string Payment, string? PaymentReference, string Status, string? ReversalStatus, string? ReversalReason, string RecordedBy,
    string? ApprovedBy, DateTimeOffset CreatedAt, string? RejectNote, IReadOnlyList<DeliveryLineDto> Lines);

public sealed record ReverseDeliveryRequest(string Reason);

public sealed record RejectStockRequest(string? Note);

/// <summary>The cost to suggest for a delivery line: the last one paid this supplier for this item and pack.</summary>
public sealed record LastCostDto(decimal? UnitCost);

public sealed class SupplierCommands(ITausiDb db, ICurrentSession session, StockAccess access, IAuditLog audit)
{
    public async Task<IReadOnlyList<SupplierDto>> List(CancellationToken ct)
    {
        await access.RequireAny(session.RequireUser(), ct);
        var owed = await Owed(db, ct);
        return (await db.Suppliers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct))
            .Select(s => new SupplierDto(s.Id, s.Name, s.Phone, s.KraPin, s.Notes, s.Active, owed.GetValueOrDefault(s.Id), s.RowVersion)).ToList();
    }

    /// <summary>What each supplier is owed: credit deliveries and opening balances less payments (Task 12).</summary>
    public static Task<Dictionary<Guid, decimal>> Owed(ITausiDb db, CancellationToken ct) => Money.SupplierBalances.Owed(db, ct);

    public async Task<SupplierDto> Save(Guid? id, SaveSupplierRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        if (await access.Check(user, PermissionKeys.StockReceive, ct) == PermissionValue.NotAllowed)
            throw new PermissionDeniedException(PermissionKeys.StockReceive);
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > Supplier.MaxNameLength) throw new DomainException("Give the supplier a name.", "name_invalid");
        if (await db.Suppliers.AnyAsync(s => s.Id != id && s.Name.ToLower() == name.ToLower(), ct))
            throw new DomainException($"There is already a supplier called {name}.", "name_taken");
        Supplier supplier;
        if (id is { } existing)
        {
            supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == existing, ct) ?? throw new NotFoundException("Supplier not found.");
            if (r.RowVersion is { } v && v != supplier.RowVersion) throw new ConflictException("Someone else changed this supplier. Reload and try again.");
        }
        else
        {
            supplier = new Supplier();
            db.Suppliers.Add(supplier);
        }
        supplier.Name = name;
        supplier.Phone = (r.Phone ?? "").Trim();
        supplier.KraPin = (r.KraPin ?? "").Trim().ToUpperInvariant();
        supplier.Notes = (r.Notes ?? "").Trim();
        supplier.Active = r.Active;
        audit.Write(new AuditRecord(AuditActions.SupplierChanged, nameof(Supplier), supplier.Id.ToString(), null,
            new { supplier.Name, supplier.Phone, supplier.KraPin, supplier.Active }));
        await db.SaveChangesAsync(ct);
        return new SupplierDto(supplier.Id, supplier.Name, supplier.Phone, supplier.KraPin, supplier.Notes, supplier.Active,
            (await Owed(db, ct)).GetValueOrDefault(supplier.Id), supplier.RowVersion);
    }
}

/// <summary>
/// Receiving a delivery (stock.receive): saved as Pending when the key needs approval, else posted
/// at once: Delivery movements in base units at the pack cost ÷ the pack factor, the new average
/// cost, and Stock against what paid for it. Reversing one (stock.adjust) takes it back out.
/// </summary>
public sealed class DeliveryCommands(
    ITausiDb db, ICurrentSession session, StockAccess access, StockBook book, ILedgerPostingService ledger,
    IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public const string LedgerSource = "Delivery";

    public async Task<DeliveryDto> Receive(ReceiveDeliveryRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        var postNow = await access.PostsNow(user, PermissionKeys.StockReceive, ct);
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.SupplierId && s.Active, ct)
            ?? throw new DomainException("Choose the supplier.");
        if (!await db.StockLocations.AnyAsync(l => l.Id == r.LocationId && l.Active, ct)) throw new DomainException("Choose where it goes.");
        var invoice = (r.InvoiceNumber ?? "").Trim();
        if (invoice.Length is 0 or > Delivery.MaxInvoiceLength) throw new DomainException("Type the supplier's invoice number.", "invoice_required");
        var zone = BusinessTimeZone.Find((await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessTimeZoneKey));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        var date = r.Date ?? today;
        if (date > today) throw new DomainException("The delivery date can't be in the future.", "date_future");
        await EnsureInvoiceUnused(supplier, invoice, ct);
        var reference = r.Payment is DeliveryPayment.Mpesa or DeliveryPayment.Bank ? (r.PaymentReference ?? "").Trim() : null;
        if (reference is { Length: 0 }) throw new DomainException("Type the M-Pesa or bank reference.", "reference_required");
        if (reference is { Length: > Delivery.MaxReferenceLength }) throw new DomainException("The reference is too long.");

        var lines = r.Lines ?? [];
        if (lines.Count == 0) throw new DomainException("Add at least one item.");
        var ids = lines.Select(l => l.StockItemId).Distinct().ToList();
        var items = await db.StockItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var delivery = new Delivery
        {
            SupplierId = supplier.Id, InvoiceNumber = invoice, Date = date, LocationId = r.LocationId, Payment = r.Payment,
            PaymentReference = reference, Status = StockDocumentStatus.Pending, CreatedByUserId = user.UserId, CreatedAt = clock.GetUtcNow(),
        };
        foreach (var line in lines)
        {
            if (!items.TryGetValue(line.StockItemId, out var item)) throw new DomainException("That stock item no longer exists.");
            if (line.Quantity <= 0) throw new DomainException($"Enter how many {item.Name} came.");
            if (line.UnitCost < 0) throw new DomainException($"The cost of {item.Name} can't be below 0.");
            var unit = string.IsNullOrWhiteSpace(line.Unit) ? item.LargestUnit : line.Unit.Trim();
            delivery.Lines.Add(new DeliveryLine
            {
                DeliveryId = delivery.Id, StockItemId = item.Id, Unit = unit, Factor = item.FactorOf(unit), Quantity = line.Quantity,
                UnitCost = line.UnitCost, LineTotal = MoneyMath.Round(line.Quantity * line.UnitCost),
            });
        }
        delivery.Total = delivery.Lines.Sum(l => l.LineTotal);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Deliveries.Add(delivery);
        audit.Write(new AuditRecord(AuditActions.DeliveryRecorded, nameof(Delivery), delivery.Id.ToString(), null,
            new { supplier = supplier.Name, delivery.InvoiceNumber, date = delivery.Date, delivery.Total, payment = delivery.Payment.ToString(), pending = !postNow }));
        if (postNow) await Post(delivery, user.UserId, null, ct);
        await Save(ct, supplier, invoice);
        await tx.CommitAsync(ct);
        await StockEvents.Publish(events, "deliveries", ct);
        return await Get(delivery.Id, ct);
    }

    /// <summary>Approve a pending delivery (an Allowed user, not who recorded it): it posts now.</summary>
    public async Task<DeliveryDto> Approve(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var delivery = await Load(id, ct);
        if (delivery.Status != StockDocumentStatus.Pending) throw new ConflictException("This delivery isn't waiting for approval.", StockCodes.NotPending);
        await access.RequireApprover(user, PermissionKeys.StockReceive, delivery.CreatedByUserId, ct);
        await Post(delivery, user.UserId, user.UserId, ct);
        audit.Write(new AuditRecord(AuditActions.StockDocumentApproved, nameof(Delivery), delivery.Id.ToString(),
            new { status = "Pending" }, new { status = "Posted" }, user.UserId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await StockEvents.Publish(events, "deliveries", ct);
        return await Get(id, ct);
    }

    public async Task<DeliveryDto> Reject(Guid id, RejectStockRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        var delivery = await Load(id, ct);
        if (delivery.Status == StockDocumentStatus.Pending)
        {
            await access.RequireApprover(user, PermissionKeys.StockReceive, delivery.CreatedByUserId, ct);
            delivery.Status = StockDocumentStatus.Rejected;
        }
        else if (delivery.ReversalStatus == StockDocumentStatus.Pending)
        {
            await access.RequireApprover(user, PermissionKeys.StockAdjust, delivery.ReversalRequestedByUserId!.Value, ct);
            delivery.ReversalStatus = StockDocumentStatus.Rejected;
        }
        else throw new ConflictException("Nothing on this delivery is waiting for approval.", StockCodes.NotPending);
        delivery.RejectNote = Note(r.Note);
        audit.Write(new AuditRecord(AuditActions.StockDocumentRejected, nameof(Delivery), delivery.Id.ToString(), null,
            new { note = delivery.RejectNote }, user.UserId));
        await db.SaveChangesAsync(ct);
        await StockEvents.Publish(events, "deliveries", ct);
        return await Get(id, ct);
    }

    /// <summary>
    /// Takes a posted delivery back out (stock.adjust, a reason): refused if what's left at that
    /// location is less than it brought (some was sold already: count instead).
    /// </summary>
    public async Task<DeliveryDto> Reverse(Guid id, ReverseDeliveryRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        var postNow = await access.PostsNow(user, PermissionKeys.StockAdjust, ct);
        var reason = Note(r.Reason) ?? throw new DomainException("Say why the delivery is reversed.", "reason_required");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var delivery = await Load(id, ct);
        if (delivery.Status != StockDocumentStatus.Posted || delivery.ReversalStatus is StockDocumentStatus.Posted or StockDocumentStatus.Pending)
            throw new ConflictException("Only a recorded delivery can be reversed, once.", StockCodes.NotPending);
        delivery.ReversalReason = reason;
        delivery.ReversalRequestedByUserId = user.UserId;
        delivery.ReversalRequestedAt = clock.GetUtcNow();
        delivery.ReversalStatus = StockDocumentStatus.Pending;
        delivery.RejectNote = null;
        if (postNow) await PostReversal(delivery, user.UserId, ct);
        else await EnsureReversible(delivery, ct);
        audit.Write(new AuditRecord(AuditActions.DeliveryReversed, nameof(Delivery), delivery.Id.ToString(), null,
            new { reason, pending = !postNow }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await StockEvents.Publish(events, "deliveries", ct);
        return await Get(id, ct);
    }

    public async Task<DeliveryDto> ApproveReversal(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var delivery = await Load(id, ct);
        if (delivery.ReversalStatus != StockDocumentStatus.Pending) throw new ConflictException("No reversal is waiting for approval.", StockCodes.NotPending);
        await access.RequireApprover(user, PermissionKeys.StockAdjust, delivery.ReversalRequestedByUserId!.Value, ct);
        await PostReversal(delivery, user.UserId, ct);
        audit.Write(new AuditRecord(AuditActions.StockDocumentApproved, nameof(Delivery), delivery.Id.ToString(),
            new { reversal = "Pending" }, new { reversal = "Posted" }, user.UserId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await StockEvents.Publish(events, "deliveries", ct);
        return await Get(id, ct);
    }

    public async Task<IReadOnlyList<DeliveryDto>> List(CancellationToken ct)
    {
        await access.RequireAny(session.RequireUser(), ct);
        var ids = await db.Deliveries.AsNoTracking().OrderByDescending(d => d.Date).ThenByDescending(d => d.CreatedAt).Take(200)
            .Select(d => d.Id).ToListAsync(ct);
        return await View(ids, ct);
    }

    public async Task<DeliveryDto> Get(Guid id, CancellationToken ct) =>
        (await View([id], ct)).SingleOrDefault() ?? throw new NotFoundException("Delivery not found.");

    /// <summary>The last cost per this pack paid to this supplier for this item (to suggest it).</summary>
    public async Task<LastCostDto> LastCost(Guid supplierId, Guid itemId, string? unit, CancellationToken ct)
    {
        await access.RequireAny(session.RequireUser(), ct);
        var last = await db.DeliveryLines.AsNoTracking()
            .Join(db.Deliveries, l => l.DeliveryId, d => d.Id, (l, d) => new { l, d })
            .Where(x => x.d.SupplierId == supplierId && x.l.StockItemId == itemId && x.d.Status != StockDocumentStatus.Rejected
                && (unit == null || x.l.Unit == unit))
            .OrderByDescending(x => x.d.Date).ThenByDescending(x => x.d.CreatedAt)
            .Select(x => (decimal?)x.l.UnitCost).FirstOrDefaultAsync(ct);
        return new LastCostDto(last);
    }

    private async Task Post(Delivery delivery, Guid userId, Guid? approvedBy, CancellationToken ct)
    {
        var day = await WorkPeriods.Current(db, ct);
        var ids = delivery.Lines.Select(l => l.StockItemId).Distinct().ToList();
        await book.LockItems(ids, ct);
        var items = await db.StockItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        foreach (var line in delivery.Lines)
        {
            var item = items[line.StockItemId];
            var onHand = await book.TotalOnHand(item.Id, ct);
            item.AverageCost = StockBook.AverageAfterDelivery(onHand, item.AverageCost, line.BaseQuantity, line.BaseUnitCost);
            await book.Move(item.Id, delivery.LocationId, line.BaseQuantity, line.BaseUnitCost, StockMovementKind.Delivery,
                StockReferences.Delivery, delivery.Id, userId, day?.Id, ct);
        }
        delivery.Status = StockDocumentStatus.Posted;
        delivery.ApprovedByUserId = approvedBy;
        delivery.PostedAt = clock.GetUtcNow();
        if (delivery.Total > 0)
            await ledger.Post(new JournalEntryDraft(clock.GetUtcNow(), await Memo(delivery, "Delivery", ct), LedgerSource, delivery.Id.ToString(),
                [JournalLineDraft.Dr(LedgerAccountCodes.Stock, delivery.Total), JournalLineDraft.Cr(PaidFrom(delivery.Payment), delivery.Total)],
                day?.Id, BusinessDate: delivery.Date), ct);
    }

    private async Task PostReversal(Delivery delivery, Guid userId, CancellationToken ct)
    {
        await EnsureReversible(delivery, ct);
        var day = await WorkPeriods.Current(db, ct);
        var ids = delivery.Lines.Select(l => l.StockItemId).Distinct().ToList();
        await book.LockItems(ids, ct);
        var items = await db.StockItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        foreach (var line in delivery.Lines)
        {
            var item = items[line.StockItemId];
            var onHand = await book.TotalOnHand(item.Id, ct);
            item.AverageCost = StockBook.AverageAfterReversal(onHand, item.AverageCost, line.BaseQuantity, line.BaseUnitCost);
            await book.Move(item.Id, delivery.LocationId, -line.BaseQuantity, line.BaseUnitCost, StockMovementKind.DeliveryReversal,
                StockReferences.Delivery, delivery.Id, userId, day?.Id, ct);
        }
        delivery.ReversalStatus = StockDocumentStatus.Posted;
        delivery.ReversalApprovedByUserId = userId == delivery.ReversalRequestedByUserId ? null : userId;
        delivery.ReversedAt = clock.GetUtcNow();
        if (delivery.Total > 0)
            await ledger.Post(new JournalEntryDraft(clock.GetUtcNow(), await Memo(delivery, "Delivery reversed", ct), LedgerSource,
                delivery.Id + ":reversal",
                [JournalLineDraft.Dr(PaidFrom(delivery.Payment), delivery.Total), JournalLineDraft.Cr(LedgerAccountCodes.Stock, delivery.Total)],
                day?.Id, BusinessDate: await Money.BusinessDates.Today(db, clock, ct)), ct);
    }

    private async Task EnsureReversible(Delivery delivery, CancellationToken ct)
    {
        foreach (var byItem in delivery.Lines.GroupBy(l => l.StockItemId))
        {
            var onHand = await db.StockLevels.Where(l => l.StockItemId == byItem.Key && l.LocationId == delivery.LocationId)
                .Select(l => l.OnHand).FirstOrDefaultAsync(ct);
            if (onHand < byItem.Sum(l => l.BaseQuantity))
                throw new ConflictException("Some of this delivery has already been sold. Count the stock instead.", StockCodes.AlreadySold);
        }
    }

    public static string PaidFrom(DeliveryPayment payment) => payment switch
    {
        DeliveryPayment.OnCredit => LedgerAccountCodes.SupplierPayables,
        DeliveryPayment.Cash => LedgerAccountCodes.Safe,
        DeliveryPayment.Mpesa => LedgerAccountCodes.MPesa,
        _ => LedgerAccountCodes.Bank,
    };

    private async Task<string> Memo(Delivery delivery, string what, CancellationToken ct) =>
        $"{what} · {await db.Suppliers.Where(s => s.Id == delivery.SupplierId).Select(s => s.Name).FirstAsync(ct)} {delivery.InvoiceNumber}";

    private async Task EnsureInvoiceUnused(Supplier supplier, string invoice, CancellationToken ct)
    {
        var lower = invoice.ToLower();
        var used = await db.Deliveries.AsNoTracking()
            .Where(d => d.SupplierId == supplier.Id && d.InvoiceNumber.ToLower() == lower && d.Status != StockDocumentStatus.Rejected)
            .Select(d => (DateOnly?)d.Date).FirstOrDefaultAsync(ct);
        if (used is { } on)
            throw new ConflictException($"Invoice {invoice} from {supplier.Name} is already recorded on {on:d MMM}.", StockCodes.InvoiceUsed);
    }

    /// <summary>Saves; if the unique index caught the same invoice saved at the same moment, says so.</summary>
    private async Task Save(CancellationToken ct, Supplier supplier, string invoice)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            (db as DbContext)?.ChangeTracker.Clear();
            await EnsureInvoiceUnused(supplier, invoice, ct);
            throw;
        }
    }

    private async Task<Delivery> Load(Guid id, CancellationToken ct) =>
        await db.Deliveries.Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Delivery not found.");

    private static string? Note(string? text)
    {
        var note = text?.Trim();
        if (note is { Length: > Delivery.MaxNoteLength }) throw new DomainException($"Keep the note under {Delivery.MaxNoteLength} characters.");
        return string.IsNullOrEmpty(note) ? null : note;
    }

    private async Task<IReadOnlyList<DeliveryDto>> View(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var deliveries = await db.Deliveries.AsNoTracking().Include(d => d.Lines).Where(d => ids.Contains(d.Id)).ToListAsync(ct);
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var locations = await db.StockLocations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var itemIds = deliveries.SelectMany(d => d.Lines).Select(l => l.StockItemId).Distinct().ToList();
        var items = await db.StockItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        var userIds = deliveries.SelectMany(d => new[] { d.CreatedByUserId, d.ApprovedByUserId ?? Guid.Empty }).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return ids.Select(id => deliveries.FirstOrDefault(d => d.Id == id)).OfType<Delivery>().Select(d => new DeliveryDto(
            d.Id, d.Date, d.SupplierId, suppliers.GetValueOrDefault(d.SupplierId, "?"), d.InvoiceNumber, d.LocationId,
            locations.GetValueOrDefault(d.LocationId, "?"), d.Total, d.Payment.ToString(), d.PaymentReference, d.Status.ToString(),
            d.ReversalStatus?.ToString(), d.ReversalReason, users.GetValueOrDefault(d.CreatedByUserId, "?"),
            d.ApprovedByUserId is { } a ? users.GetValueOrDefault(a) : null, d.CreatedAt, d.RejectNote,
            d.Lines.Select(l => new DeliveryLineDto(l.StockItemId, items.GetValueOrDefault(l.StockItemId, "?"), l.Unit, l.Factor, l.Quantity,
                l.UnitCost, l.LineTotal)).ToList())).ToList();
    }
}
