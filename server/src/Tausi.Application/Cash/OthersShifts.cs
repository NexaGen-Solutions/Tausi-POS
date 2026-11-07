using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Ledger;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Tickets;
using Tausi.Domain.Cash;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Cash;

/// <summary>A pending (not completed) payment a shift still holds, on a part-paid ticket.</summary>
public sealed record PendingPaymentDto(Guid Id, Guid TicketId, long TicketNumber, string Type, decimal Amount);

/// <summary>An open shift in the Open shifts list (1.0.2): whose, where, since when, cash taken so far.</summary>
public sealed record OpenShiftRowDto(
    Guid Id, Guid CashierId, string Cashier, Guid TerminalId, string Terminal, DateTimeOffset OpenedAt, decimal CashTaken,
    bool Counted, string? ClosingBy, IReadOnlyList<PendingPaymentDto> Pending);

/// <summary>The count of another cashier's shift, with why it is closed.</summary>
public sealed record CountOthersShiftRequest(CashCount Count, string Reason, string? Note = null);

/// <summary>
/// The sign-off: another supervisor's PIN on the approval pad (its token in the request header), or
/// the closer alone, ticking "I counted this cash alone".
/// </summary>
public sealed record CloseOthersShiftRequest(bool CountedAlone = false);

/// <summary>"Your shift on Counter A was closed by SUPER1 on Sun 23:40. Difference: −100."</summary>
public sealed record ShiftNoticeDto(Guid ShiftId, string Terminal, string ClosedBy, DateTimeOffset ClosedAt, decimal Difference, string? Reason);

/// <summary>
/// Supervisors close a shift its cashier left open (Task 19, v1.0.2), with
/// <c>cash.close_others_shift</c> (Allowed only: the closer is the approver). From any terminal or the
/// admin site: list the open shifts, count the drawer (blind if <c>cashup.blind</c>), deal with its
/// pending payments, and close with a sign-off by another <c>day.open_close</c> user's PIN, or alone
/// ("I counted this cash alone", flagged). The difference, the move to the safe and the lock are as
/// for a cashier's own close; the slip prints on the closer's terminal; the cashier gets a notice.
/// </summary>
public sealed class OthersShifts(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval, ShiftReader reader,
    ILedgerPostingService ledger, CashPrints prints, IAuditLog audit, TicketEvents events, TimeProvider clock)
{
    /// <summary>Whether this user may close other cashiers' shifts.</summary>
    public async Task<bool> MayClose(SessionUser user, CancellationToken ct) =>
        await permissions.Check(user, PermissionKeys.CashCloseOthersShift, ct) == PermissionValue.Allowed;

    private async Task<SessionUser> Require(CancellationToken ct)
    {
        var user = session.RequireUser();
        if (!await MayClose(user, ct)) throw new PermissionDeniedException(PermissionKeys.CashCloseOthersShift);
        return user;
    }

    /// <summary>Every open shift (training ones on training terminals only), oldest first.</summary>
    public async Task<IReadOnlyList<OpenShiftRowDto>> List(CancellationToken ct)
    {
        await Require(ct);
        var shifts = await db.CashierShifts.AsNoTracking().Where(s => s.Status == ShiftStatus.Open).ToListAsync(ct);
        var list = new List<OpenShiftRowDto>();
        foreach (var shift in shifts.OrderBy(s => s.OpenedAt)) list.Add(await Row(shift, ct));
        return list;
    }

    private async Task<OpenShiftRowDto> Row(CashierShift shift, CancellationToken ct)
    {
        var names = await reader.Names([shift.CashierUserId, .. shift.ClosedByUserId is { } c ? new[] { c } : []], ct);
        var terminal = await db.Terminals.AsNoTracking().Where(t => t.Id == shift.TerminalId).Select(t => t.Name).FirstAsync(ct);
        var figures = await reader.Figures(shift.Id, ct);
        var closing = shift.ClosedByOther ? names.GetValueOrDefault(shift.ClosedByUserId!.Value) : null;
        return new OpenShiftRowDto(shift.Id, shift.CashierUserId, names.GetValueOrDefault(shift.CashierUserId, "?"), shift.TerminalId, terminal,
            shift.OpenedAt, figures.CashSettled, shift.CountedAt is not null, closing, await Pending(shift.Id, ct));
    }

    public async Task<IReadOnlyList<PendingPaymentDto>> Pending(Guid shiftId, CancellationToken ct) =>
        await (from p in db.Payments.AsNoTracking()
               join k in db.Tickets.AsNoTracking() on p.TicketId equals k.Id
               where p.ShiftId == shiftId && p.Status == PaymentStatus.Pending
               select new PendingPaymentDto(p.Id, k.Id, k.Number, p.Type.ToString(), p.Amount)).ToListAsync(ct);

    /// <summary>The shift as the closer sees it: with a blind count, no expected cash until counted.</summary>
    public async Task<ShiftDto> View(Guid id, CancellationToken ct)
    {
        var user = await Require(ct);
        return await reader.View(await Other(id, user, ct), user.UserId, ct, counting: true);
    }

    /// <summary>The closer counts the drawer by denomination and says why.</summary>
    public async Task<ShiftDto> Count(Guid id, CountOthersShiftRequest r, CancellationToken ct)
    {
        var user = await Require(ct);
        var shift = await Other(id, user, ct);
        if (shift.ClosedByUserId is { } by && by != user.UserId && shift.ClosedByUserId != shift.CashierUserId)
            throw new ConflictException("Another supervisor is closing this shift.", ShiftCodes.ClosingByOther);
        if (shift.ClosedByUserId is null && shift.CountedAt is not null)
            throw new ConflictException("Its cashier counted it already. Sign it off from Close day as usual.", ShiftCodes.AlreadyCounted);
        var settings = await CashSettings.Load(db, ct);
        if (settings.BlindCount && shift.CountedAt is not null)
            throw new ConflictException("The count was already submitted. Sign it off now.", ShiftCodes.AlreadyCounted);
        var reason = CashierShift.CloseReasons.FirstOrDefault(x => string.Equals(x, r.Reason?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new FieldErrorsException(new Dictionary<string, string> { ["reason"] = "Choose why you are closing this shift." });
        var note = r.Note?.Trim();
        if (reason == "Other" && string.IsNullOrEmpty(note))
            throw new FieldErrorsException(new Dictionary<string, string> { ["note"] = "Say why you are closing this shift." });
        if (note is { Length: > CashierShift.MaxCloseNoteLength })
            throw new FieldErrorsException(new Dictionary<string, string> { ["note"] = $"At most {CashierShift.MaxCloseNoteLength} characters." });

        var counted = (r.Count ?? new CashCount()).Amount(settings.Denominations);
        var expected = (await reader.Figures(shift.Id, ct)).Expected(shift.OpeningFloat);
        shift.CountJson = (r.Count ?? new CashCount(Total: counted)).ToJson() ?? System.Text.Json.JsonSerializer.Serialize(new { coins = counted });
        shift.Counted = counted;
        shift.CountedAt = clock.GetUtcNow();
        shift.Expected = expected;
        shift.Difference = counted - expected;
        shift.ClosedByUserId = user.UserId;
        shift.CloseReason = reason;
        shift.CloseNote = string.IsNullOrEmpty(note) ? null : note;
        audit.Write(new AuditRecord(AuditActions.ShiftCounted, nameof(CashierShift), shift.Id.ToString(), null,
            new { counted, expected, difference = shift.Difference, blind = settings.BlindCount, cashier = shift.CashierUserId, by = user.Name, reason }));
        await db.SaveChangesAsync(ct);
        return await reader.View(shift, user.UserId, ct, counting: true);
    }

    /// <summary>Moves the shift's pending payments to the closer's own open shift on this terminal.</summary>
    public async Task<IReadOnlyList<PendingPaymentDto>> MovePending(Guid id, CancellationToken ct)
    {
        var user = await Require(ct);
        var shift = await Other(id, user, ct);
        if (user.TerminalId is not { } terminalId || await reader.OpenFor(user.UserId, terminalId, ct) is not { } mine)
            throw new ConflictException("Moving them needs your own shift open on this terminal. Start your shift, or remove them.", ShiftCodes.NoShiftHere);
        var pending = await db.Payments.Where(p => p.ShiftId == shift.Id && p.Status == PaymentStatus.Pending).ToListAsync(ct);
        foreach (var p in pending)
        {
            p.ShiftId = mine.Id;
            p.TakenByUserId = user.UserId;
        }
        audit.Write(new AuditRecord(AuditActions.ShiftPendingMoved, nameof(CashierShift), shift.Id.ToString(), null,
            new { to = mine.Id, payments = pending.Select(p => new { p.Id, p.TicketId, type = p.Type.ToString(), p.Amount }) }));
        await db.SaveChangesAsync(ct);
        return await Pending(shift.Id, ct);
    }

    /// <summary>Removes the shift's pending payments: their tickets go back to unpaid.</summary>
    public async Task<IReadOnlyList<PendingPaymentDto>> RemovePending(Guid id, CancellationToken ct)
    {
        var user = await Require(ct);
        var shift = await Other(id, user, ct);
        var pending = await db.Payments.Where(p => p.ShiftId == shift.Id && p.Status == PaymentStatus.Pending).ToListAsync(ct);
        var now = clock.GetUtcNow();
        foreach (var p in pending)
        {
            p.Status = PaymentStatus.Removed;
            p.RemovedAt = now;
            p.RemovedByUserId = user.UserId;
            audit.Write(new AuditRecord(AuditActions.PaymentRemoved, nameof(Ticket), p.TicketId.ToString(),
                new { type = p.Type.ToString(), p.Amount, p.MpesaCode }, null));
        }
        await db.SaveChangesAsync(ct);
        foreach (var ticketId in pending.Select(p => p.TicketId).Distinct())
            if (await db.Tickets.AsNoTracking().FirstOrDefaultAsync(k => k.Id == ticketId, ct) is { } ticket)
                await events.Changed(ticket, ticket.TableId, ct);
        return await Pending(shift.Id, ct);
    }

    /// <summary>Closes the counted shift: signed off by another user's PIN, or by the closer alone (flagged).</summary>
    public async Task<ShiftDto> Close(Guid id, CloseOthersShiftRequest r, CancellationToken ct)
    {
        var user = await Require(ct);
        // Not tracked: the close claims the row with one update, as a cashier's own close does.
        var shift = await Other(id, user, ct, tracked: false);
        if (shift.ClosedByUserId != user.UserId || shift.CountedAt is null || shift.Counted is not { } counted)
            throw new ConflictException("Count the cash and submit the count first.", ShiftCodes.NotCounted);
        if ((await Pending(shift.Id, ct)).Count > 0)
            throw new ConflictException("This shift still holds pending payments. Move them to your shift or remove them first.", ShiftCodes.PendingPayments);

        Guid signedOffBy;
        if (r.CountedAlone)
        {
            // Nobody else is there: the closer signs off, if they could sign off a shift anyway.
            if (await permissions.Check(user, PermissionKeys.DayOpenClose, ct) != PermissionValue.Allowed)
                throw new ApprovalRequiredException(PermissionKeys.DayOpenClose, "Ask a supervisor to sign it off with their PIN.");
            signedOffBy = user.UserId;
        }
        else
        {
            signedOffBy = await permissions.RequireSignOff(PermissionKeys.DayOpenClose, approval.ApprovalToken, shift.CashierUserId, ct);
            if (signedOffBy == user.UserId)
                throw new ConflictException("You counted it, so someone else signs it off. Or tick \"I counted this cash alone\".", ShiftCodes.SignOffNeeded);
        }

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
                .SetProperty(s => s.CountedAlone, r.CountedAlone)
                .SetProperty(s => s.RowVersion, s => s.RowVersion + 1), ct);
        if (claimed != 1) throw new ConflictException("This shift was closed by someone else just now.", ShiftCodes.Closed);
        shift.Status = ShiftStatus.Closed;
        shift.ClosedAt = now;
        shift.SignedOffByUserId = signedOffBy;
        shift.Expected = expected;
        shift.Difference = difference;
        shift.CountedAlone = r.CountedAlone;

        if (ShiftPosting.Close(shift, counted, difference, now) is { } entry) await ledger.Post(entry, ct);
        var view = await reader.View(shift, user.UserId, ct);
        // On the closer's terminal: the cashier may be gone, and the drawer counted elsewhere.
        var job = user.TerminalId is { } here
            ? await prints.Print(here, (style, business, local) => CashSlips.CashUp(view, business, local, style), ct)
            : null;
        audit.Write(new AuditRecord(AuditActions.ShiftClosed, nameof(CashierShift), shift.Id.ToString(),
            new { status = ShiftStatus.Open.ToString() },
            new
            {
                status = ShiftStatus.Closed.ToString(), cashier = view.Cashier, closedBy = user.Name, reason = shift.CloseReason, note = shift.CloseNote,
                counted, expected, difference, countedAlone = r.CountedAlone,
                flag = r.CountedAlone ? "Closed and signed off by the same person" : null, slip = job?.Id,
            }, signedOffBy));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return view;
    }

    /// <summary>An open shift that isn't the user's own (their own closes as usual).</summary>
    private async Task<CashierShift> Other(Guid id, SessionUser user, CancellationToken ct, bool tracked = true)
    {
        var shifts = tracked ? db.CashierShifts : db.CashierShifts.AsNoTracking();
        var shift = await shifts.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Shift not found.");
        if (shift.CashierUserId == user.UserId)
            throw new ConflictException("This is your own shift: close it from My shift.", "own_shift");
        if (!shift.IsOpen) throw new ConflictException("This shift is closed already.", ShiftCodes.Closed);
        return shift;
    }
}

/// <summary>The notice a cashier sees at their next login when someone else closed their shift; OK dismisses it.</summary>
public sealed class ShiftNotices(ITausiDb db, ICurrentSession session, TimeProvider clock)
{
    public async Task<IReadOnlyList<ShiftNoticeDto>> Mine(CancellationToken ct)
    {
        var user = session.RequireUser();
        var shifts = await db.CashierShifts.AsNoTracking()
            .Where(s => s.CashierUserId == user.UserId && s.Status == ShiftStatus.Closed && s.ClosedByUserId != null
                        && s.ClosedByUserId != user.UserId && s.CashierNoticeSeenAt == null)
            .Select(s => new
            {
                s.Id, s.ClosedAt, s.Difference, s.CloseReason,
                Terminal = db.Terminals.Where(t => t.Id == s.TerminalId).Select(t => t.Name).FirstOrDefault(),
                ClosedBy = db.Users.Where(u => u.Id == s.ClosedByUserId).Select(u => u.Name).FirstOrDefault(),
            }).ToListAsync(ct);
        return shifts.Where(s => s.ClosedAt is not null)
            .Select(s => new ShiftNoticeDto(s.Id, s.Terminal ?? "?", s.ClosedBy ?? "?", s.ClosedAt!.Value, s.Difference ?? 0, s.CloseReason))
            .OrderBy(n => n.ClosedAt).ToList();
    }

    public async Task Dismiss(Guid shiftId, CancellationToken ct)
    {
        var user = session.RequireUser();
        await db.CashierShifts.Where(s => s.Id == shiftId && s.CashierUserId == user.UserId && s.CashierNoticeSeenAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.CashierNoticeSeenAt, clock.GetUtcNow()), ct);
    }
}
