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

public sealed record StartCountRequest(Guid LocationId, StockCountKind Kind, bool IsOpening, List<Guid>? ItemIds);

public sealed record CountEntry(Guid StockItemId, Dictionary<string, decimal>? Entered, decimal? UnitCost);

public sealed record EnterCountsRequest(List<CountEntry>? Lines);

public sealed record CountUnitDto(string Name, decimal Factor);

/// <summary>
/// One item of a count. Before it is submitted the expected figure, the difference and the value are
/// null (a blind count); an opening count never has them and takes a unit cost instead.
/// </summary>
public sealed record CountLineDto(
    Guid StockItemId, string StockItem, string BaseUnit, IReadOnlyList<CountUnitDto> Units, IReadOnlyDictionary<string, decimal> Entered,
    decimal? Counted, string? CountedText, decimal? Expected, string? ExpectedText, decimal? Difference, decimal? Value, decimal? UnitCost);

public sealed record CountDto(
    Guid Id, Guid LocationId, string Location, string Kind, bool IsOpening, string Status, DateTimeOffset AsAt, string CountedBy,
    DateTimeOffset? SubmittedAt, string? ApprovedBy, DateTimeOffset? ApprovedAt, string? RejectNote, bool DayOpen, bool Blind,
    int Uncounted, int ItemsShort, decimal? VarianceValue, bool CanApprove, IReadOnlyList<CountLineDto> Lines, long RowVersion);

public sealed record CountListItem(
    Guid Id, DateTimeOffset AsAt, string Location, string Kind, bool IsOpening, string CountedBy, string? ApprovedBy, string Status,
    decimal? VarianceValue);

public sealed record PrintCountSheetRequest(Guid PrinterId);

/// <summary>
/// Stock counts of one location. Started as Draft with an as-at time; the counter types what is
/// there (by pack and base unit) without seeing the expected figures; on submit (stock.count) the
/// expected figures as at the start are filled in; someone else Allowed stock.adjust approves it,
/// which sets stock to the counted figures and posts the difference as stock loss (or gain).
/// </summary>
public sealed class StockCountCommands(
    ITausiDb db, ICurrentSession session, StockAccess access, StockBook book, ILedgerPostingService ledger, PrintQueue queue,
    IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public const string LedgerSource = "StockCount";

    public async Task<CountDto> Start(StartCountRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        await RequireCounter(user, ct);
        var location = await db.StockLocations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == r.LocationId && l.Active, ct)
            ?? throw new DomainException("Choose the location to count.");
        if (await db.StockCounts.AnyAsync(c => c.LocationId == r.LocationId && (c.Status == StockCountStatus.Draft || c.Status == StockCountStatus.Submitted), ct))
            throw new ConflictException($"{location.Name} has a count in progress. Finish or approve it first.", "count_open");
        if (r.IsOpening && await db.StockCounts.AnyAsync(c => c.LocationId == r.LocationId && c.Status == StockCountStatus.Approved, ct))
            throw new DomainException($"{location.Name} has been counted before: only the first count can be the opening stock.", "not_first");

        var items = db.StockItems.AsNoTracking().Where(i => i.Active);
        if (r.Kind == StockCountKind.Partial)
        {
            var ids = r.ItemIds ?? [];
            if (ids.Count == 0) throw new DomainException("Pick the items to count.");
            items = items.Where(i => ids.Contains(i.Id));
        }
        var count = new StockCount
        {
            LocationId = location.Id, Kind = r.Kind, IsOpening = r.IsOpening, Status = StockCountStatus.Draft,
            AsAt = clock.GetUtcNow(), CountedByUserId = user.UserId,
        };
        foreach (var id in await items.OrderBy(i => i.Category).ThenBy(i => i.Name).Select(i => i.Id).ToListAsync(ct))
            count.Lines.Add(new StockCountLine { CountId = count.Id, StockItemId = id });
        if (count.Lines.Count == 0) throw new DomainException("There are no stock items to count.");
        db.StockCounts.Add(count);
        audit.Write(new AuditRecord(AuditActions.StockCountChanged, nameof(StockCount), count.Id.ToString(), null,
            new { status = "Draft", location = location.Name, kind = r.Kind.ToString(), r.IsOpening, asAt = count.AsAt }));
        await db.SaveChangesAsync(ct);
        await StockEvents.Publish(events, "counts", ct);
        return await Get(count.Id, ct);
    }

    /// <summary>Saves what was counted so far (a draft can be filled in over time).</summary>
    public async Task<CountDto> Enter(Guid id, EnterCountsRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        await RequireCounter(user, ct);
        var count = await Load(id, ct);
        if (count.Status != StockCountStatus.Draft) throw new ConflictException("This count was submitted already.", StockCodes.NotPending);
        var ids = count.Lines.Select(l => l.StockItemId).ToList();
        var items = await db.StockItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        foreach (var entry in r.Lines ?? [])
        {
            var line = count.Lines.FirstOrDefault(l => l.StockItemId == entry.StockItemId) ?? throw new DomainException("That item isn't in this count.");
            var item = items[line.StockItemId];
            var entered = new Dictionary<string, decimal>();
            decimal? total = null;
            foreach (var (unit, quantity) in entry.Entered ?? [])
            {
                if (quantity < 0) throw new DomainException($"A count of {item.Name} can't be below 0.");
                var factor = item.FactorOf(unit);
                entered[unit] = quantity;
                total = (total ?? 0) + quantity * factor;
            }
            line.Entered = entered;
            line.Counted = total;
            if (count.IsOpening)
            {
                if (entry.UnitCost is < 0) throw new DomainException($"The cost of {item.Name} can't be below 0.");
                line.UnitCost = entry.UnitCost;
            }
        }
        await db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    public async Task<CountDto> Submit(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        await RequireCounter(user, ct);
        var count = await Load(id, ct);
        if (count.Status != StockCountStatus.Draft) throw new ConflictException("This count was submitted already.", StockCodes.NotPending);
        var uncounted = count.Lines.Count(l => l.Counted is null);
        if (uncounted > 0)
            throw new DomainException(uncounted == 1 ? "1 item not counted. Enter 0 if there are none." : $"{uncounted} items not counted. Enter 0 if there are none.",
                StockCodes.Uncounted);
        if (count.IsOpening && count.Lines.Any(l => l.Counted > 0 && l.UnitCost is null))
            throw new DomainException("Opening stock needs a cost per unit for each item there is.", "cost_required");

        var costs = await Costs(count, ct);
        foreach (var line in count.Lines) line.Expected = await OnHandAt(count.LocationId, line.StockItemId, count.AsAt, ct);
        count.VarianceValue = count.IsOpening ? null : MoneyMath.Round(count.Lines.Sum(l => l.Difference!.Value * costs[l.StockItemId]));
        count.Status = StockCountStatus.Submitted;
        count.SubmittedAt = clock.GetUtcNow();
        count.CountedByUserId = user.UserId;
        count.RejectNote = null;
        audit.Write(new AuditRecord(AuditActions.StockCountChanged, nameof(StockCount), count.Id.ToString(),
            new { status = "Draft" }, new { status = "Submitted", variance = count.VarianceValue }));
        await db.SaveChangesAsync(ct);
        await StockEvents.Publish(events, "counts", ct);
        return await Get(id, ct);
    }

    /// <summary>
    /// Sets stock to the counted figures: the adjustment is counted − expected as at the start, so
    /// sales during the count are kept. Short goes to Stock loss, over comes back from it; an opening
    /// count sets each item's cost and posts against Opening balances.
    /// </summary>
    public async Task<CountDto> Approve(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var count = await Load(id, ct);
        if (count.Status != StockCountStatus.Submitted) throw new ConflictException("This count isn't waiting for approval.", StockCodes.NotPending);
        await access.RequireApprover(user, PermissionKeys.StockAdjust, count.CountedByUserId, ct);
        var day = await WorkPeriods.Current(db, ct);
        var ids = count.Lines.Select(l => l.StockItemId).ToList();
        await book.LockItems(ids, ct);
        var items = await db.StockItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

        decimal shorts = 0, overs = 0;
        foreach (var line in count.Lines)
        {
            var item = items[line.StockItemId];
            var difference = line.Difference ?? 0;
            if (count.IsOpening && line.Counted > 0 && line.UnitCost is { } cost) item.AverageCost = cost;
            line.UnitCost ??= item.AverageCost;
            if (difference == 0) continue;
            var moved = await book.Move(item.Id, count.LocationId, difference, item.AverageCost,
                count.IsOpening ? StockMovementKind.Opening : StockMovementKind.CountAdjust, StockReferences.Count, count.Id, user.UserId, day?.Id, ct);
            if (moved.Value < 0) shorts -= moved.Value;
            else overs += moved.Value;
        }
        shorts = MoneyMath.Round(shorts);
        overs = MoneyMath.Round(overs);
        var other = count.IsOpening ? LedgerAccountCodes.OpeningBalances : LedgerAccountCodes.StockLoss;
        var lines = new List<JournalLineDraft>();
        if (overs > 0) lines.AddRange([JournalLineDraft.Dr(LedgerAccountCodes.Stock, overs), JournalLineDraft.Cr(other, overs)]);
        if (shorts > 0) lines.AddRange([JournalLineDraft.Dr(other, shorts), JournalLineDraft.Cr(LedgerAccountCodes.Stock, shorts)]);
        var location = await db.StockLocations.Where(l => l.Id == count.LocationId).Select(l => l.Name).FirstAsync(ct);
        if (lines.Count > 0)
            await ledger.Post(new JournalEntryDraft(clock.GetUtcNow(), $"{(count.IsOpening ? "Opening stock" : "Stock count")} · {location}",
                LedgerSource, count.Id.ToString(), lines, day?.Id), ct);

        count.VarianceValue = count.IsOpening ? null : overs - shorts;
        count.Status = StockCountStatus.Approved;
        count.ApprovedByUserId = user.UserId;
        count.ApprovedAt = clock.GetUtcNow();
        audit.Write(new AuditRecord(AuditActions.StockCountChanged, nameof(StockCount), count.Id.ToString(),
            new { status = "Submitted" }, new { status = "Approved", variance = count.VarianceValue }, user.UserId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await StockEvents.Publish(events, "counts", ct);
        return await Get(id, ct);
    }

    /// <summary>Back to Draft with a note: the expected figures are hidden again until it is resubmitted.</summary>
    public async Task<CountDto> Reject(Guid id, RejectStockRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        var count = await Load(id, ct);
        if (count.Status != StockCountStatus.Submitted) throw new ConflictException("This count isn't waiting for approval.", StockCodes.NotPending);
        await access.RequireApprover(user, PermissionKeys.StockAdjust, count.CountedByUserId, ct);
        count.Status = StockCountStatus.Draft;
        count.RejectNote = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim()[..Math.Min(r.Note.Trim().Length, StockCount.MaxNoteLength)];
        count.VarianceValue = null;
        foreach (var line in count.Lines) line.Expected = null;
        audit.Write(new AuditRecord(AuditActions.StockCountChanged, nameof(StockCount), count.Id.ToString(),
            new { status = "Submitted" }, new { status = "Draft", note = count.RejectNote }, user.UserId));
        await db.SaveChangesAsync(ct);
        await StockEvents.Publish(events, "counts", ct);
        return await Get(id, ct);
    }

    public async Task<IReadOnlyList<CountListItem>> List(CancellationToken ct)
    {
        await access.RequireAny(session.RequireUser(), ct);
        var counts = await db.StockCounts.AsNoTracking().OrderByDescending(c => c.AsAt).Take(200).ToListAsync(ct);
        var locations = await db.StockLocations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return counts.Select(c => new CountListItem(c.Id, c.AsAt, locations.GetValueOrDefault(c.LocationId, "?"), c.Kind.ToString(), c.IsOpening,
            users.GetValueOrDefault(c.CountedByUserId, "?"), c.ApprovedByUserId is { } a ? users.GetValueOrDefault(a) : null, c.Status.ToString(),
            c.VarianceValue)).ToList();
    }

    public async Task<CountDto> Get(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        await access.RequireAny(user, ct);
        var count = await db.StockCounts.AsNoTracking().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundException("Count not found.");
        var ids = count.Lines.Select(l => l.StockItemId).ToList();
        var items = await db.StockItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var location = await db.StockLocations.AsNoTracking().Where(l => l.Id == count.LocationId).Select(l => l.Name).FirstAsync(ct);
        var users = await db.Users.AsNoTracking().Where(u => u.Id == count.CountedByUserId || u.Id == count.ApprovedByUserId)
            .ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        // Blind until submitted: the API itself leaves the expected figures out.
        var blind = count.Status == StockCountStatus.Draft || count.IsOpening;
        var lines = count.Lines
            .OrderBy(l => items[l.StockItemId].Category).ThenBy(l => items[l.StockItemId].Name)
            .Select(l =>
            {
                var item = items[l.StockItemId];
                var cost = l.UnitCost ?? item.AverageCost;
                return new CountLineDto(
                    item.Id, item.Name, item.BaseUnitName,
                    new[] { new CountUnitDto(item.BaseUnitName, 1) }.Concat(item.Packs.OrderByDescending(p => p.Factor).Select(p => new CountUnitDto(p.Name, p.Factor))).ToList(),
                    l.Entered, l.Counted, l.Counted is { } c ? item.Describe(c) : null,
                    blind ? null : l.Expected, blind || l.Expected is null ? null : item.Describe(l.Expected.Value),
                    blind ? null : l.Difference, blind || l.Difference is null ? null : MoneyMath.Round(l.Difference.Value * cost),
                    count.IsOpening ? l.UnitCost : null);
            }).ToList();
        var canApprove = count.Status == StockCountStatus.Submitted && count.CountedByUserId != user.UserId
            && await access.Check(user, PermissionKeys.StockAdjust, ct) == PermissionValue.Allowed;
        return new CountDto(
            count.Id, count.LocationId, location, count.Kind.ToString(), count.IsOpening, count.Status.ToString(), count.AsAt,
            users.GetValueOrDefault(count.CountedByUserId, "?"), count.SubmittedAt,
            count.ApprovedByUserId is { } a ? users.GetValueOrDefault(a) : null, count.ApprovedAt, count.RejectNote,
            await WorkPeriods.Current(db, ct) is not null, blind, count.Lines.Count(l => l.Counted is null),
            blind ? 0 : count.Lines.Count(l => l.Difference < 0), blind ? null : count.VarianceValue, canApprove, lines, count.RowVersion);
    }

    /// <summary>The A4 count sheet: every item with a box per pack and base unit, and no expected figures.</summary>
    public async Task<byte[]> SheetPdf(Guid id, CancellationToken ct)
    {
        var (location, asAt, items) = await Sheet(id, ct);
        var pdf = new SimplePdf();
        const float left = 40, right = SimplePdf.PageWidth - 40;
        var business = (await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessName);
        float y = 0;
        void Header()
        {
            pdf.Text(left, 50, $"Stock count · {location}", 18, bold: true);
            pdf.Text(left, 70, $"{business} · as at {asAt:dd MMM yyyy HH:mm} · counted by ____________________", 10);
            pdf.Text(left, 98, "Stock item", 9, bold: true);
            pdf.Text(300, 98, "Count (write each pack and loose units)", 9, bold: true);
            pdf.Line(left, 104, right, 104);
            y = 124;
        }
        Header();
        foreach (var (name, units) in items)
        {
            if (y > SimplePdf.PageHeight - 60)
            {
                pdf.NewPage();
                Header();
            }
            pdf.Text(left, y, name, 11);
            var x = 300f;
            foreach (var unit in units)
            {
                pdf.Box(x, y - 13, 42, 18);
                pdf.Text(x + 46, y, unit, 9);
                x += 46 + SimplePdf.Width(unit, 9) + 14;
            }
            pdf.Line(left, y + 9, right, y + 9, 0.25f);
            y += 28;
        }
        return pdf.Build();
    }

    /// <summary>The same sheet on a receipt printer (optional).</summary>
    public async Task PrintSheet(Guid id, PrintCountSheetRequest r, CancellationToken ct)
    {
        var (location, asAt, items) = await Sheet(id, ct);
        var printer = await db.Printers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.PrinterId && p.Active, ct)
            ?? throw new DomainException("Choose a printer that is switched on.");
        var zone = Domain.Settings.BusinessTimeZone.Find((await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessTimeZoneKey));
        queue.Enqueue(printer, PrintJobKind.Stock,
            StockSlips.CountSheet(location, BusinessClock.ToLocal(asAt, zone), items, new SlipStyle(printer.Columns, printer.CutAfterSlip, null)), null);
        await db.SaveChangesAsync(ct);
    }

    private async Task<(string Location, DateTimeOffset AsAt, IReadOnlyList<(string Item, IReadOnlyList<string> Units)> Items)> Sheet(Guid id, CancellationToken ct)
    {
        await access.RequireAny(session.RequireUser(), ct);
        var count = await db.StockCounts.AsNoTracking().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Count not found.");
        var ids = count.Lines.Select(l => l.StockItemId).ToList();
        var items = await db.StockItems.AsNoTracking().Where(i => ids.Contains(i.Id)).OrderBy(i => i.Category).ThenBy(i => i.Name).ToListAsync(ct);
        var location = await db.StockLocations.AsNoTracking().Where(l => l.Id == count.LocationId).Select(l => l.Name).FirstAsync(ct);
        var zone = Domain.Settings.BusinessTimeZone.Find((await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessTimeZoneKey));
        return (location, BusinessClock.ToLocal(count.AsAt, zone),
            items.Select(i => (i.Name, (IReadOnlyList<string>)i.Packs.OrderByDescending(p => p.Factor).Select(p => p.Name).Append(i.BaseUnitName).ToList())).ToList());
    }

    /// <summary>What the movements say was at this location at a moment.</summary>
    public Task<decimal> OnHandAt(Guid locationId, Guid itemId, DateTimeOffset at, CancellationToken ct) =>
        db.StockMovements.Where(m => m.LocationId == locationId && m.StockItemId == itemId && m.At <= at).SumAsync(m => m.Quantity, ct);

    private async Task<Dictionary<Guid, decimal>> Costs(StockCount count, CancellationToken ct)
    {
        var ids = count.Lines.Select(l => l.StockItemId).ToList();
        return await db.StockItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.AverageCost, ct);
    }

    private async Task RequireCounter(SessionUser user, CancellationToken ct)
    {
        if (await access.Check(user, PermissionKeys.StockCount, ct) == PermissionValue.NotAllowed)
            throw new PermissionDeniedException(PermissionKeys.StockCount);
    }

    private async Task<StockCount> Load(Guid id, CancellationToken ct) =>
        await db.StockCounts.Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Count not found.");
}
