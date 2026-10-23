using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Ledger;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Cash;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Domain.People;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;
using Tausi.Domain.Settings;

namespace Tausi.Application.Cash;

public static class ShiftCodes
{
    public const string ShiftRequired = "shift_required";
    public const string DrawerInUse = "drawer_in_use";
    public const string AlreadyOpen = "shift_already_open";
    public const string NotCounted = "not_counted";
    public const string AlreadyCounted = "already_counted";
    public const string Closed = "shift_closed";
    public const string BelowZero = "drawer_below_zero";
    public const string PendingCash = "pending_cash";
}

/// <summary>
/// Cash by denomination: notes by value ("1000": 2) plus coins as one amount; or just a total.
/// </summary>
public sealed record CashCount(IReadOnlyDictionary<string, int>? Notes = null, decimal? Coins = null, decimal? Total = null)
{
    public decimal Amount(IReadOnlyList<int> denominations)
    {
        if (Notes is null && Coins is null) return Total ?? 0m;
        var notes = 0m;
        foreach (var (key, count) in Notes ?? new Dictionary<string, int>())
        {
            if (!int.TryParse(key, out var value) || !denominations.Contains(value))
                throw new DomainException($"{key} isn't one of the notes counted here.");
            if (count < 0 || count > 100_000) throw new DomainException("Enter how many notes of each, from 0.");
            notes += value * count;
        }
        var coins = Coins ?? 0m;
        if (coins < 0 || decimal.Round(coins, 2) != coins) throw new DomainException("Enter the coins as an amount of 0 or more.");
        return notes + coins;
    }

    public string? ToJson() => Notes is null && Coins is null ? null : JsonSerializer.Serialize(new { notes = Notes, coins = Coins ?? 0m });

    public static IReadOnlyDictionary<string, int> NotesFrom(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new Dictionary<string, int>();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Object
            ? notes.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32())
            : new Dictionary<string, int>();
    }

    public static decimal CoinsFrom(string? json)
    {
        if (string.IsNullOrEmpty(json)) return 0m;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("coins", out var coins) ? coins.GetDecimal() : 0m;
    }
}

/// <param name="Float">The opening float by denomination or as a total; null takes <c>cash.defaultFloat</c>.</param>
public sealed record OpenShiftRequest(CashCount? Float = null);

public sealed record CountShiftRequest(CashCount Count, string? Note = null);

/// <param name="CategoryId">Pay-outs: what it was for. Drops: none.</param>
public sealed record DrawerMovementRequest(decimal Amount, Guid? CategoryId = null, string? Note = null);

public sealed record WaiterCashDto(Guid UserId, string Name, decimal Cash, int OpenBills);

public sealed record DrawerMovementDto(Guid Id, string Kind, decimal Amount, string? Category, string? Note, string By, DateTimeOffset At);

public sealed record ExpenseCategoryDto(Guid Id, string Name);

/// <summary>
/// A shift as the cash-up screen shows it. With a blind count, the cashier gets
/// <see cref="Expected"/> and <see cref="Difference"/> as null until the count is submitted.
/// </summary>
public sealed record ShiftDto(
    Guid Id, string Status, Guid CashierId, string Cashier, Guid TerminalId, string Terminal,
    DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt,
    decimal OpeningFloat, decimal CashSettled, decimal PayOuts, decimal Drops, decimal? Expected,
    int MpesaCount, decimal MpesaTotal, int CardCount, decimal CardTotal,
    bool Blind, bool CountSubmitted, IReadOnlyDictionary<string, int> CountNotes, decimal CountCoins, decimal? Counted, decimal? Difference,
    string? Note, string? SignedOffBy, int Receipts, long? FirstReceipt, long? LastReceipt,
    IReadOnlyList<int> Denominations, IReadOnlyList<WaiterCashDto> Waiters, IReadOnlyList<DrawerMovementDto> Movements,
    IReadOnlyList<ExpenseCategoryDto> Categories);

/// <summary>"Start shift" defaults for a cashier with none open.</summary>
public sealed record ShiftStartDto(decimal DefaultFloat, IReadOnlyList<int> Denominations);

/// <summary>GET /api/pos/shifts/current: my open shift on this terminal, or how to start one.</summary>
public sealed record CurrentShiftDto(ShiftDto? Shift, ShiftStartDto Start);

/// <summary>The figures of a shift, worked out from its payments and drawer movements.</summary>
public sealed record ShiftFigures(
    decimal CashSettled, decimal PayOuts, decimal Drops, int MpesaCount, decimal MpesaTotal, int CardCount, decimal CardTotal,
    IReadOnlyList<(string Category, decimal Amount)> PayOutsByCategory)
{
    public decimal Expected(decimal openingFloat) => openingFloat + CashSettled - PayOuts - Drops;
}

/// <summary>Reading shifts and working out their figures.</summary>
public sealed class ShiftReader(ITausiDb db)
{
    /// <summary>The user's open shift on this terminal, or null.</summary>
    public Task<CashierShift?> OpenFor(Guid userId, Guid terminalId, CancellationToken ct) =>
        db.CashierShifts.FirstOrDefaultAsync(s => s.CashierUserId == userId && s.TerminalId == terminalId && s.Status == ShiftStatus.Open, ct);

    /// <summary>
    /// Expected cash = float + cash applied in settles (not tendered; pending ones are in the drawer
    /// too) − pay-outs − drops. M-Pesa and card are shown for checking only.
    /// </summary>
    public async Task<ShiftFigures> Figures(Guid shiftId, CancellationToken ct)
    {
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.ShiftId == shiftId && p.Status != PaymentStatus.Removed)
            .Select(p => new { p.Type, p.Amount }).ToListAsync(ct);
        var moves = await db.DrawerMovements.AsNoTracking().Where(m => m.ShiftId == shiftId)
            .Select(m => new { m.Kind, m.Amount, m.CategoryName }).ToListAsync(ct);
        return new ShiftFigures(
            payments.Where(p => p.Type == PaymentType.Cash).Sum(p => p.Amount),
            moves.Where(m => m.Kind == DrawerMovementKind.PayOut).Sum(m => m.Amount),
            moves.Where(m => m.Kind == DrawerMovementKind.Drop).Sum(m => m.Amount),
            payments.Count(p => p.Type == PaymentType.Mpesa), payments.Where(p => p.Type == PaymentType.Mpesa).Sum(p => p.Amount),
            payments.Count(p => p.Type == PaymentType.Card), payments.Where(p => p.Type == PaymentType.Card).Sum(p => p.Amount),
            moves.Where(m => m.Kind == DrawerMovementKind.PayOut).GroupBy(m => m.CategoryName ?? "Other")
                .Select(g => (g.Key, g.Sum(m => m.Amount))).ToList());
    }

    /// <summary>The cash-up screen's view of a shift. <paramref name="viewer"/> decides what a blind count hides.</summary>
    public async Task<ShiftDto> View(CashierShift shift, Guid viewer, CancellationToken ct)
    {
        var settings = await CashSettings.Load(db, ct);
        var figures = await Figures(shift.Id, ct);
        var cashier = await db.Users.AsNoTracking().Where(u => u.Id == shift.CashierUserId).Select(u => u.Name).FirstAsync(ct);
        var terminal = await db.Terminals.AsNoTracking().Where(t => t.Id == shift.TerminalId).Select(t => t.Name).FirstAsync(ct);
        var signedOff = shift.SignedOffByUserId is { } s
            ? await db.Users.AsNoTracking().Where(u => u.Id == s).Select(u => u.Name).FirstOrDefaultAsync(ct) : null;
        var counted = shift.CountedAt is not null;
        // A blind count hides the expected cash from the cashier until they have counted.
        var hide = settings.BlindCount && !counted && viewer == shift.CashierUserId && shift.IsOpen;
        var expected = figures.Expected(shift.OpeningFloat);

        var receipts = await db.Tickets.AsNoTracking().Where(k => k.SettledShiftId == shift.Id && k.ReceiptNumber != null)
            .Select(k => k.ReceiptNumber!.Value).ToListAsync(ct);
        var moves = await db.DrawerMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id).ToListAsync(ct);
        var names = await Names(moves.Select(m => m.ByUserId), ct);
        var categories = await db.ExpenseCategories.AsNoTracking().Where(c => c.Active).OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new ExpenseCategoryDto(c.Id, c.Name)).ToListAsync(ct);

        return new ShiftDto(
            shift.Id, shift.Status.ToString(), shift.CashierUserId, cashier, shift.TerminalId, terminal, shift.OpenedAt, shift.ClosedAt,
            shift.OpeningFloat, figures.CashSettled, figures.PayOuts, figures.Drops, hide ? null : shift.Expected ?? expected,
            figures.MpesaCount, figures.MpesaTotal, figures.CardCount, figures.CardTotal,
            settings.BlindCount, counted, CashCount.NotesFrom(shift.CountJson), CashCount.CoinsFrom(shift.CountJson),
            shift.Counted, hide ? null : shift.Difference ?? (shift.Counted is { } c ? c - expected : null),
            shift.Note, signedOff, receipts.Count, receipts.Count > 0 ? receipts.Min() : null, receipts.Count > 0 ? receipts.Max() : null,
            settings.Denominations, await Waiters(shift, ct),
            moves.OrderBy(m => m.At).Select(m => new DrawerMovementDto(m.Id, m.Kind.ToString(), m.Amount, m.CategoryName, m.Note,
                names.GetValueOrDefault(m.ByUserId, "?"), m.At)).ToList(),
            categories);
    }

    /// <summary>Cash each waiter brought in this shift, and the bills still open on their name.</summary>
    public async Task<IReadOnlyList<WaiterCashDto>> Waiters(CashierShift shift, CancellationToken ct)
    {
        var brought = await db.Payments.AsNoTracking()
            .Where(p => p.ShiftId == shift.Id && p.Status != PaymentStatus.Removed && p.Type == PaymentType.Cash)
            .Select(p => new { p.BroughtByUserId, p.Amount }).ToListAsync(ct);
        var open = await db.Tickets.AsNoTracking().Where(k => k.Status == TicketStatus.Open)
            .Select(k => k.OpenedByUserId).ToListAsync(ct);
        var ids = brought.Select(b => b.BroughtByUserId).Distinct().ToList();
        var names = await Names(ids, ct);
        return ids.Select(id => new WaiterCashDto(id, names.GetValueOrDefault(id, "?"),
                brought.Where(b => b.BroughtByUserId == id).Sum(b => b.Amount), open.Count(o => o == id)))
            .OrderBy(w => w.Name).ToList();
    }

    public async Task<Dictionary<Guid, string>> Names(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
    }
}

/// <summary>Drawer entries in the ledger. Each carries the shift, the user and the work period.</summary>
public static class ShiftPosting
{
    public const string SourceType = "Shift";

    public static JournalEntryDraft? Float(CashierShift shift, DateTimeOffset now) =>
        shift.OpeningFloat <= 0 ? null : new JournalEntryDraft(now, "Opening float", SourceType, shift.Id.ToString(),
            [JournalLineDraft.Dr(LedgerAccountCodes.Cash, shift.OpeningFloat, "Float into the drawer"),
             JournalLineDraft.Cr(LedgerAccountCodes.Safe, shift.OpeningFloat, "Float from the safe")],
            shift.WorkPeriodId, shift.Id);

    public static JournalEntryDraft Movement(CashierShift shift, DrawerMovement m, DateTimeOffset now) =>
        m.Kind == DrawerMovementKind.PayOut
            ? new JournalEntryDraft(now, $"Pay out · {m.CategoryName}", SourceType, m.Id.ToString(),
                [JournalLineDraft.Dr(LedgerAccountCodes.Expenses, m.Amount, m.CategoryName ?? "Other"),
                 JournalLineDraft.Cr(LedgerAccountCodes.Cash, m.Amount, "Paid from the drawer")],
                shift.WorkPeriodId, shift.Id)
            : new JournalEntryDraft(now, "Cash drop", SourceType, m.Id.ToString(),
                [JournalLineDraft.Dr(LedgerAccountCodes.Safe, m.Amount, "Dropped to the safe"),
                 JournalLineDraft.Cr(LedgerAccountCodes.Cash, m.Amount, "From the drawer")],
                shift.WorkPeriodId, shift.Id);

    /// <summary>The difference to Cash over/short, then what is left in the drawer to the safe.</summary>
    public static JournalEntryDraft? Close(CashierShift shift, decimal counted, decimal difference, DateTimeOffset now)
    {
        var lines = new List<JournalLineDraft>();
        if (difference < 0)
        {
            lines.Add(JournalLineDraft.Dr(LedgerAccountCodes.CashOverShort, -difference, "Cash short"));
            lines.Add(JournalLineDraft.Cr(LedgerAccountCodes.Cash, -difference, "Cash short"));
        }
        else if (difference > 0)
        {
            lines.Add(JournalLineDraft.Dr(LedgerAccountCodes.Cash, difference, "Cash over"));
            lines.Add(JournalLineDraft.Cr(LedgerAccountCodes.CashOverShort, difference, "Cash over"));
        }
        if (counted > 0)
        {
            lines.Add(JournalLineDraft.Dr(LedgerAccountCodes.Safe, counted, "Drawer to the safe"));
            lines.Add(JournalLineDraft.Cr(LedgerAccountCodes.Cash, counted, "Drawer to the safe"));
        }
        return lines.Count == 0 ? null
            : new JournalEntryDraft(now, "Cash-up", SourceType, shift.Id.ToString(), lines, shift.WorkPeriodId, shift.Id);
    }
}

/// <summary>Cash slips (cash-up, pay-out, drop) on a terminal's receipt printer; nothing prints without one.</summary>
public sealed class CashPrints(ITausiDb db, PrintQueue queue)
{
    public async Task<PrintJob?> Print(Guid terminalId, Func<SlipStyle, string, Func<DateTimeOffset, DateTimeOffset>, PrintDocument> build, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking().Include(t => t.ReceiptPrinter).FirstAsync(t => t.Id == terminalId, ct);
        if (terminal.ReceiptPrinter is not { Active: true } printer) return null;
        var business = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct) ?? new BusinessSettings();
        var zone = BusinessTimeZone.Find(business.TimeZone);
        var document = build(SlipStyle.For(printer, null, business.Currency), business.Name, at => BusinessClock.ToLocal(at, zone));
        return queue.Enqueue(printer, PrintJobKind.CashUp, document);
    }
}

/// <summary>Opens a shift on this terminal's drawer: one per cashier and one per drawer, inside an open day.</summary>
public sealed class OpenShift(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    ShiftReader reader, ILedgerPostingService ledger, IAuditLog audit, TimeProvider clock)
{
    public async Task<ShiftDto> Execute(OpenShiftRequest request, CancellationToken ct)
    {
        var user = session.RequireUser();
        if (user.TerminalId is not { } terminalId)
            throw new ForbiddenException("A shift is opened on a terminal (its drawer).", "terminal_required");
        await permissions.Require(user, PermissionKeys.TicketSettle, approval.ApprovalToken, ct);
        var day = await WorkPeriods.Current(db, ct)
            ?? throw new DomainException("The day isn't open yet. A supervisor must open it first.", "day_closed");
        await EnsureFree(user.UserId, terminalId, ct);

        var settings = await CashSettings.Load(db, ct);
        var @float = request.Float?.Amount(settings.Denominations) ?? settings.DefaultFloat;
        if (@float < 0 || decimal.Round(@float, 2) != @float) throw new DomainException("Enter the float as an amount of 0 or more.");
        var now = clock.GetUtcNow();
        var shift = new CashierShift
        {
            CashierUserId = user.UserId, TerminalId = terminalId, WorkPeriodId = day.Id, OpeningFloat = @float,
            FloatCountsJson = request.Float?.ToJson(), OpenedAt = now,
        };
        db.CashierShifts.Add(shift);
        if (ShiftPosting.Float(shift, now) is { } entry) await ledger.Post(entry, ct);
        audit.Write(new AuditRecord(AuditActions.ShiftOpened, nameof(CashierShift), shift.Id.ToString(), null,
            new { terminal = user.TerminalName, openingFloat = @float }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            // Two terminals at once: the unique index let only one through.
            (db as DbContext)?.ChangeTracker.Clear();
            await EnsureFree(user.UserId, terminalId, ct);
            throw;
        }
        return await reader.View(shift, user.UserId, ct);
    }

    private async Task EnsureFree(Guid userId, Guid terminalId, CancellationToken ct)
    {
        var mine = await db.CashierShifts.AsNoTracking().Where(s => s.CashierUserId == userId && s.Status == ShiftStatus.Open)
            .Select(s => db.Terminals.Where(t => t.Id == s.TerminalId).Select(t => t.Name).FirstOrDefault()).FirstOrDefaultAsync(ct);
        if (mine is not null)
            throw new ConflictException($"You already have a shift open on {mine}. Close it first.", ShiftCodes.AlreadyOpen);
        var drawer = await db.CashierShifts.AsNoTracking().Where(s => s.TerminalId == terminalId && s.Status == ShiftStatus.Open)
            .Select(s => new
            {
                Terminal = db.Terminals.Where(t => t.Id == s.TerminalId).Select(t => t.Name).FirstOrDefault(),
                Cashier = db.Users.Where(u => u.Id == s.CashierUserId).Select(u => u.Name).FirstOrDefault(),
            }).FirstOrDefaultAsync(ct);
        if (drawer is not null)
            throw new ConflictException(
                $"{drawer.Terminal}'s drawer is in {drawer.Cashier}'s shift. {drawer.Cashier} must close it first.", ShiftCodes.DrawerInUse);
    }
}

/// <summary>My open shift on this terminal (or the start-shift defaults), and any shift's summary.</summary>
public sealed class ShiftQueries(ITausiDb db, ICurrentSession session, IPermissionService permissions, ShiftReader reader)
{
    public async Task<CurrentShiftDto> Current(CancellationToken ct)
    {
        var user = session.RequireUser();
        var settings = await CashSettings.Load(db, ct);
        var start = new ShiftStartDto(settings.DefaultFloat, settings.Denominations);
        if (user.TerminalId is not { } terminalId) return new CurrentShiftDto(null, start);
        var shift = await db.CashierShifts.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CashierUserId == user.UserId && s.TerminalId == terminalId && s.Status == ShiftStatus.Open, ct);
        return new CurrentShiftDto(shift is null ? null : await reader.View(shift, user.UserId, ct), start);
    }

    /// <summary>The cashier's own shift, or any shift for someone with <c>day.open_close</c>.</summary>
    public async Task<ShiftDto> Summary(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        var shift = await db.CashierShifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new NotFoundException("Shift not found.");
        if (shift.CashierUserId != user.UserId
            && await permissions.Check(user, PermissionKeys.DayOpenClose, ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(PermissionKeys.DayOpenClose);
        return await reader.View(shift, user.UserId, ct);
    }
}

/// <summary>
/// The cashier submits the count. With a blind count it can be submitted once, and only then do
/// the expected cash and the difference show; without it, it can be counted again until sign-off.
/// </summary>
public sealed class CountShift(ITausiDb db, ICurrentSession session, ShiftReader reader, IAuditLog audit, TimeProvider clock)
{
    public async Task<ShiftDto> Execute(Guid id, CountShiftRequest request, CancellationToken ct)
    {
        var user = session.RequireUser();
        var shift = await OwnOpenShift.Load(db, id, user.UserId, ct);
        var settings = await CashSettings.Load(db, ct);
        if (settings.BlindCount && shift.CountedAt is not null)
            throw new ConflictException("The count was already submitted. A supervisor signs it off now.", ShiftCodes.AlreadyCounted);
        var note = request.Note?.Trim();
        if (note is { Length: > CashierShift.MaxNoteLength }) throw new DomainException($"The note can be at most {CashierShift.MaxNoteLength} characters.");

        var counted = (request.Count ?? new CashCount()).Amount(settings.Denominations);
        var expected = (await reader.Figures(shift.Id, ct)).Expected(shift.OpeningFloat);
        shift.CountJson = (request.Count ?? new CashCount(Total: counted)).ToJson() ?? JsonSerializer.Serialize(new { coins = counted });
        shift.Counted = counted;
        shift.CountedAt = clock.GetUtcNow();
        shift.Expected = expected;
        shift.Difference = counted - expected;
        shift.Note = string.IsNullOrEmpty(note) ? null : note;
        audit.Write(new AuditRecord(AuditActions.ShiftCounted, nameof(CashierShift), shift.Id.ToString(), null,
            new { counted, expected, difference = shift.Difference, blind = settings.BlindCount }));
        await db.SaveChangesAsync(ct);
        return await reader.View(shift, user.UserId, ct);
    }
}

internal static class OwnOpenShift
{
    public static async Task<CashierShift> Load(ITausiDb db, Guid id, Guid userId, CancellationToken ct)
    {
        var shift = await db.CashierShifts.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Shift not found.");
        if (shift.CashierUserId != userId) throw new ForbiddenException("This is another cashier's shift.", "not_your_shift");
        if (!shift.IsOpen) throw new ConflictException("This shift is closed and can't be changed.", ShiftCodes.Closed);
        return shift;
    }
}

/// <summary>
/// Closes a counted shift with a supervisor's sign-off (their PIN on the approval pad; never the
/// cashier's own): stores and posts the difference, moves the drawer to the safe and prints the
/// cash-up slip. Of two sign-offs at once exactly one wins. Cash taken on part-paid tickets must
/// be settled or removed first, so the drawer and the books agree.
/// </summary>
public sealed class CloseShift(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    ShiftReader reader, ILedgerPostingService ledger, CashPrints prints, IAuditLog audit, TimeProvider clock)
{
    public async Task<ShiftDto> Execute(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        var shift = await db.CashierShifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Shift not found.");
        if (shift.CashierUserId != user.UserId
            && await permissions.Check(user, PermissionKeys.DayOpenClose, ct) != PermissionValue.Allowed)
            throw new ForbiddenException("This is another cashier's shift.", "not_your_shift");
        if (!shift.IsOpen) throw new ConflictException("This shift is closed already.", ShiftCodes.Closed);
        if (shift.CountedAt is null || shift.Counted is not { } counted)
            throw new ConflictException("Count the cash and submit the count first.", ShiftCodes.NotCounted);
        var pending = await db.Payments.AsNoTracking()
            .Where(p => p.ShiftId == shift.Id && p.Status == PaymentStatus.Pending && p.Type == PaymentType.Cash)
            .Select(p => db.Tickets.Where(k => k.Id == p.TicketId).Select(k => k.Number).First()).Distinct().ToListAsync(ct);
        if (pending.Count > 0)
            throw new ConflictException(
                $"Cash taken on part-paid ticket{(pending.Count == 1 ? "" : "s")} {string.Join(", ", pending.Select(n => $"#{n}"))} is still pending. Settle them or remove the payments first.",
                ShiftCodes.PendingCash);

        var signedOffBy = await permissions.RequireSignOff(PermissionKeys.DayOpenClose, approval.ApprovalToken, shift.CashierUserId, ct);
        var now = clock.GetUtcNow();
        var figures = await reader.Figures(shift.Id, ct);
        var expected = figures.Expected(shift.OpeningFloat);
        var difference = counted - expected;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.CashierShifts.Where(s => s.Id == id && s.Status == ShiftStatus.Open)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, ShiftStatus.Closed)
                .SetProperty(s => s.ClosedAt, now)
                .SetProperty(s => s.SignedOffByUserId, signedOffBy)
                .SetProperty(s => s.Expected, expected)
                .SetProperty(s => s.Difference, difference)
                .SetProperty(s => s.RowVersion, s => s.RowVersion + 1), ct);
        if (claimed != 1) throw new ConflictException("This shift was closed by someone else just now.", ShiftCodes.Closed);
        shift.Status = ShiftStatus.Closed;
        shift.ClosedAt = now;
        shift.SignedOffByUserId = signedOffBy;
        shift.Expected = expected;
        shift.Difference = difference;

        if (ShiftPosting.Close(shift, counted, difference, now) is { } entry) await ledger.Post(entry, ct);
        var view = await reader.View(shift, user.UserId, ct);
        var job = await prints.Print(shift.TerminalId, (style, business, local) => CashSlips.CashUp(view, business, local, style), ct);
        audit.Write(new AuditRecord(AuditActions.ShiftClosed, nameof(CashierShift), shift.Id.ToString(),
            new { status = ShiftStatus.Open.ToString() },
            new { status = ShiftStatus.Closed.ToString(), counted, expected, difference, slip = job?.Id }, signedOffBy));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return view;
    }
}

/// <summary>
/// Pay-outs (cash for an expense; needs <c>cash.expense</c>) and drops to the safe (needs
/// <c>ticket.settle</c>), by the cashier who owns the open shift. Neither may take the expected
/// drawer cash below 0. Each is posted, audited and prints a small slip.
/// </summary>
public sealed class DrawerMovements(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    ShiftReader reader, ILedgerPostingService ledger, CashPrints prints, IAuditLog audit, TimeProvider clock)
{
    public Task<ShiftDto> PayOut(Guid shiftId, DrawerMovementRequest r, CancellationToken ct) =>
        Move(shiftId, DrawerMovementKind.PayOut, r, ct);

    public Task<ShiftDto> Drop(Guid shiftId, DrawerMovementRequest r, CancellationToken ct) =>
        Move(shiftId, DrawerMovementKind.Drop, r, ct);

    private async Task<ShiftDto> Move(Guid shiftId, DrawerMovementKind kind, DrawerMovementRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        var shift = await OwnOpenShift.Load(db, shiftId, user.UserId, ct);
        if (shift.CountedAt is not null)
            throw new ConflictException("The cash is counted already. Close the shift first.", ShiftCodes.AlreadyCounted);
        // Everything is checked before an approval token is used up.
        if (r.Amount <= 0 || decimal.Round(r.Amount, 2) != r.Amount) throw new DomainException("Enter an amount above 0.");
        var note = r.Note?.Trim();
        if (note is { Length: > DrawerMovement.MaxNoteLength }) throw new DomainException($"The note can be at most {DrawerMovement.MaxNoteLength} characters.");
        ExpenseCategory? category = null;
        if (kind == DrawerMovementKind.PayOut)
        {
            category = await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Id == r.CategoryId && c.Active, ct)
                ?? throw new DomainException("Choose what the money was for.");
            if (string.IsNullOrEmpty(note)) throw new DomainException("Add a note saying what was bought or paid.");
        }
        var expected = (await reader.Figures(shift.Id, ct)).Expected(shift.OpeningFloat);
        if (r.Amount > expected)
            throw new DomainException($"The drawer should only have {expected:#,##0.##}; that would take it below 0.", ShiftCodes.BelowZero);
        var grant = await permissions.Require(user, kind == DrawerMovementKind.PayOut ? PermissionKeys.CashExpense : PermissionKeys.TicketSettle,
            approval.ApprovalToken, ct);

        var now = clock.GetUtcNow();
        var move = new DrawerMovement
        {
            ShiftId = shift.Id, WorkPeriodId = shift.WorkPeriodId, Kind = kind, Amount = r.Amount, CategoryId = category?.Id,
            CategoryName = category?.Name, Note = string.IsNullOrEmpty(note) ? null : note, ByUserId = user.UserId,
            ApprovedByUserId = grant.ApprovedByUserId, TerminalId = shift.TerminalId, At = now,
        };
        db.DrawerMovements.Add(move);
        await ledger.Post(ShiftPosting.Movement(shift, move, now), ct);
        var approver = grant.ApprovedByUserId is { } a ? (await reader.Names([a], ct)).GetValueOrDefault(a) : null;
        var job = await prints.Print(shift.TerminalId,
            (style, business, local) => CashSlips.Movement(move, business, user.TerminalName ?? "", user.Name, approver, local, style), ct);
        audit.Write(new AuditRecord(kind == DrawerMovementKind.PayOut ? AuditActions.PayOut : AuditActions.CashDrop,
            nameof(CashierShift), shift.Id.ToString(), null,
            new { amount = r.Amount, category = category?.Name, note, slip = job?.Id }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        return await reader.View(shift, user.UserId, ct);
    }
}
