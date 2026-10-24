using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Approvals;
using Tausi.Application.Auditing;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Events;
using Tausi.Application.Ledger;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Settling;

public static class SettleCodes
{
    public const string MpesaCodeUsed = "mpesa_code_used";
    public const string MpesaCodeInvalid = "mpesa_code_invalid";
    public const string Unprinted = "unprinted";
    public const string NotPaid = "not_paid";
    public const string Overpaid = "over_remaining";
    public const string PaymentFinal = "payment_final";
    public const string CloseVoidedOff = "close_voided_off";
    public const string NotFullyVoided = "not_fully_voided";
}

/// <param name="Amount">M-Pesa and card: the amount (defaults to what's remaining). Cash: ignored, see Tendered.</param>
/// <param name="Tendered">Cash: what the customer handed over; the change is worked out.</param>
/// <param name="BroughtByUserId">Who brought the money; defaults to the user who opened the ticket.</param>
public sealed record AddPaymentRequest(
    PaymentType Type, decimal? Amount = null, decimal? Tendered = null, string? MpesaCode = null,
    string? CardReference = null, Guid? BroughtByUserId = null);

/// <param name="Change">The change to give for the cash taken (0 without cash).</param>
/// <param name="LogOut">The session ended (pos.logoutAfterSettle): back to the PIN pad.</param>
public sealed record SettleResult(Guid TicketId, long ReceiptNumber, decimal Total, decimal Change, bool LogOut);

/// <summary>Someone who may have brought money in, for "Cash brought by".</summary>
public sealed record BroughtByDto(Guid UserId, string Name, string Role, bool IsDefault);

/// <summary>A ticket settled in the current work period, for the cashier's Settled tab.</summary>
public sealed record SettledTicketDto(
    Guid Id, long ReceiptNumber, long Number, string Where, decimal Total, IReadOnlyList<string> PaymentTypes,
    string Cashier, DateTimeOffset SettledAt);

/// <summary>
/// Payments are added and removed one at a time while settling, each saved at once as Pending.
/// Every action needs <c>ticket.settle</c> (which can't be approved for someone else) and this
/// terminal's edit lock.
/// </summary>
public sealed class PaymentCommands(
    ITausiDb db, TicketEditing editing, IPermissionService permissions, IApprovalTokenSource approval,
    Cash.ShiftReader shifts, IAuditLog audit, TicketEvents events, TimeProvider clock)
{
    public async Task<TicketDetailDto> Add(Guid ticketId, AddPaymentRequest r, CancellationToken ct)
    {
        await using var edit = await editing.Open(ticketId, ct);
        await permissions.Require(edit.User, PermissionKeys.TicketSettle, approval.ApprovalToken, ct);
        var ticket = edit.Ticket;
        ticket.ApplyRounding(await MoneySettings.Rounding(db, ct));
        var remaining = ticket.Remaining;
        if (remaining <= 0) throw new DomainException("Nothing is left to pay.", SettleCodes.Overpaid);
        var day = await WorkPeriods.Current(db, ct) ?? throw new DomainException("The day isn't open.");
        var shift = await SettleShift.Require(shifts, edit.User.UserId, edit.TerminalId, ct);

        var rules = await PaymentSettings.Load(db, ct);
        // Not recorded: the waiter who opened the ticket, whatever the request says.
        var broughtBy = rules.RecordBroughtBy ? r.BroughtByUserId ?? ticket.OpenedByUserId : ticket.OpenedByUserId;
        if (!await db.Users.AnyAsync(u => u.Id == broughtBy, ct)) throw new DomainException("Choose who brought it.");

        var payment = new Payment
        {
            TicketId = ticket.Id, Type = r.Type, TakenByUserId = edit.User.UserId, BroughtByUserId = broughtBy,
            TerminalId = edit.TerminalId, WorkPeriodId = day.Id, ShiftId = shift.Id, TakenAt = clock.GetUtcNow(),
        };
        switch (r.Type)
        {
            case PaymentType.Cash:
            {
                var tendered = r.Tendered ?? r.Amount ?? 0;
                CheckMoney(tendered, "cash received");
                payment.Tendered = tendered;
                payment.Amount = Math.Min(tendered, remaining);
                payment.Change = tendered - payment.Amount;
                break;
            }
            case PaymentType.Mpesa:
            {
                // Optional when the setting is off, but a code that is typed must still be right and unused.
                var code = string.IsNullOrWhiteSpace(r.MpesaCode) && !rules.MpesaCodeRequired
                    ? null
                    : Payment.CleanMpesaCode(r.MpesaCode)
                      ?? throw new DomainException("Type the 10-character M-Pesa code from the SMS.", SettleCodes.MpesaCodeInvalid);
                if (code is not null) await EnsureCodeUnused(code, ct);
                payment.MpesaCode = code;
                payment.Amount = Within(r.Amount ?? remaining, remaining, "M-Pesa");
                break;
            }
            case PaymentType.Card:
            {
                var reference = r.CardReference?.Trim();
                if (reference is { Length: > Payment.MaxReferenceLength })
                    throw new DomainException($"The card reference can be at most {Payment.MaxReferenceLength} characters.");
                payment.CardReference = string.IsNullOrEmpty(reference) ? null : reference;
                payment.Amount = Within(r.Amount ?? remaining, remaining, "card");
                break;
            }
            default:
                throw new DomainException("Choose cash, M-Pesa or card.");
        }

        ticket.Payments.Add(payment);
        db.Payments.Add(payment);
        ticket.Touch(payment.TakenAt);
        audit.Write(new AuditRecord(AuditActions.PaymentAdded, nameof(Ticket), ticket.Id.ToString(), null,
            new { ticket = ticket.Number, type = r.Type.ToString(), payment.Amount, payment.Tendered, payment.Change, payment.MpesaCode, payment.CardReference, broughtBy }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (payment.MpesaCode is not null && ex is not DbUpdateConcurrencyException)
        {
            // The unique index caught a code used at the same moment elsewhere.
            await edit.Transaction.RollbackAsync(ct);
            (db as DbContext)?.ChangeTracker.Clear();
            await EnsureCodeUnused(payment.MpesaCode, ct);
            throw;
        }
        await edit.Transaction.CommitAsync(ct);
        await events.Changed(ticket, ticket.TableId, ct);
        return await TicketDetails.Mine(db, ticket, edit.User, ct);
    }

    public async Task<TicketDetailDto> Remove(Guid ticketId, Guid paymentId, CancellationToken ct)
    {
        await using var edit = await editing.Open(ticketId, ct);
        await permissions.Require(edit.User, PermissionKeys.TicketSettle, approval.ApprovalToken, ct);
        var payment = edit.Ticket.Payments.FirstOrDefault(p => p.Id == paymentId)
            ?? throw new NotFoundException("That payment is not on this ticket.");
        if (payment.Status != PaymentStatus.Pending)
            throw new DomainException("This payment is final and can't be removed.", SettleCodes.PaymentFinal);
        var now = clock.GetUtcNow();
        payment.Status = PaymentStatus.Removed;
        payment.RemovedAt = now;
        payment.RemovedByUserId = edit.User.UserId;
        edit.Ticket.Touch(now);
        audit.Write(new AuditRecord(AuditActions.PaymentRemoved, nameof(Ticket), edit.Ticket.Id.ToString(),
            new { ticket = edit.Ticket.Number, type = payment.Type.ToString(), payment.Amount, payment.MpesaCode }, null));
        await db.SaveChangesAsync(ct);
        await edit.Transaction.CommitAsync(ct);
        await events.Changed(edit.Ticket, edit.Ticket.TableId, ct);
        return await TicketDetails.Mine(db, edit.Ticket, edit.User, ct);
    }

    /// <summary>"Code SJK3XXXXXX was already used on #1031 (Table 3, 20:14)", removed payments included.</summary>
    private async Task EnsureCodeUnused(string code, CancellationToken ct)
    {
        var used = await db.Payments.AsNoTracking().Where(p => p.MpesaCode == code)
            .Select(p => new { p.TicketId, p.TakenAt }).FirstOrDefaultAsync(ct);
        if (used is null) return;
        var ticket = await db.Tickets.AsNoTracking().Include(k => k.Table).FirstAsync(k => k.Id == used.TicketId, ct);
        var at = BusinessClock.ToLocal(used.TakenAt, Domain.Settings.BusinessTimeZone.Find(
            await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).Select(s => s.TimeZone).FirstOrDefaultAsync(ct)));
        throw new ConflictException(
            $"Code {code} was already used on #{ticket.Number} ({TicketPrints.Where(ticket)}, {at:HH:mm}).", SettleCodes.MpesaCodeUsed);
    }

    private static void CheckMoney(decimal amount, string what)
    {
        if (amount <= 0 || decimal.Round(amount, 2) != amount || amount > 10_000_000m)
            throw new DomainException($"Enter the {what} as an amount above 0.");
    }

    private static decimal Within(decimal amount, decimal remaining, string what)
    {
        CheckMoney(amount, $"{what} amount");
        if (amount > remaining)
            throw new DomainException($"The {what} amount can't be more than the {remaining:#,##0.##} remaining.", SettleCodes.Overpaid);
        return amount;
    }
}

/// <summary>
/// Completes a ticket: in one transaction with the row locked, it checks nothing waits for
/// approval, nothing is unprinted and nothing remains to pay; then it settles the ticket with
/// the next receipt number, makes the payments final, posts the sale to the ledger, queues the
/// receipt (and the cash drawer pulse), releases the lock and logs out if the setting says so.
/// A repeat with the same Idempotency-Key gets the same answer and never posts twice.
/// </summary>
public sealed class SettleTicket(
    ITausiDb db, ICurrentSession session, TicketEditing editing, IPermissionService permissions, IApprovalTokenSource approval,
    ITicketDatabase tickets, ILedgerPostingService ledger, TicketPrints prints, SessionKeeper sessions,
    Cash.ShiftReader shifts, IAuditLog audit, IPosEvents posEvents, TicketEvents events, TimeProvider clock)
{
    public async Task<SettleResult> Execute(Guid ticketId, CancellationToken ct)
    {
        var settled = await Settle(ticketId, ct);
        var settings = await PosSettings.Load(db, ct);
        var user = session.RequireUser();
        if (settings.LogoutAfterSettle && user.SessionId is { } sessionId)
        {
            await sessions.End(sessionId, SessionEndReasons.Logout, ct);
            return settled with { LogOut = true };
        }
        return settled;
    }

    private async Task<SettleResult> Settle(Guid ticketId, CancellationToken ct)
    {
        await using var edit = await editing.Open(ticketId, ct);
        await permissions.Require(edit.User, PermissionKeys.TicketSettle, approval.ApprovalToken, ct);
        var ticket = edit.Ticket;

        await SettleChecks.EnsureNoPendingRequests(db, ticket.Id, ct);
        var unprinted = ticket.Lines.Count(l => l.Status == OrderLineStatus.New);
        if (unprinted > 0)
            throw new ConflictException(
                unprinted == 1 ? "1 item hasn't been printed." : $"{unprinted} items haven't been printed.", SettleCodes.Unprinted);
        ticket.ApplyRounding(await MoneySettings.Rounding(db, ct));
        if (ticket.Remaining != 0)
            throw new ConflictException(
                ticket.Remaining > 0 ? $"{ticket.Remaining:#,##0.##} is still to pay." : "The payments are more than the amount due.",
                SettleCodes.NotPaid);
        var day = await WorkPeriods.Current(db, ct) ?? throw new DomainException("The day isn't open.");
        var shift = await SettleShift.Require(shifts, edit.User.UserId, edit.TerminalId, ct);

        var now = clock.GetUtcNow();
        ticket.Status = TicketStatus.Settled;
        ticket.SettledAt = now;
        ticket.SettledByUserId = edit.User.UserId;
        ticket.SettledTerminalId = edit.TerminalId;
        ticket.SettledShiftId = shift.Id;
        ticket.ReceiptNumber = await tickets.NextReceiptNumber(ct);
        ticket.ClearLock();
        ticket.Touch(now);
        var payments = ticket.Payments.Where(p => p.Counts).OrderBy(p => p.TakenAt).ToList();
        foreach (var payment in payments) payment.Status = PaymentStatus.Final;

        var draft = SalePosting.Draft(ticket, payments, day.Id, now);
        if (draft is not null) await ledger.Post(draft, ct);

        var receipt = await prints.Receipt(ticket, edit.TerminalId, copy: false, ct);
        var expired = await ApprovalExpiry.ForTickets(db, [ticket.Id], ApprovalExpiry.TicketClosed, now, ct);
        var change = payments.Sum(p => p.Change ?? 0);
        audit.Write(new AuditRecord(AuditActions.TicketSettled, nameof(Ticket), ticket.Id.ToString(),
            new { ticket = ticket.Number, status = TicketStatus.Open.ToString() },
            new
            {
                status = TicketStatus.Settled.ToString(), receipt = ticket.ReceiptNumber, total = ticket.Total,
                rounding = ticket.RoundingAmount, due = ticket.AmountDue, change, receiptJob = receipt?.Id,
                payments = payments.Select(p => new { type = p.Type.ToString(), p.Amount, p.MpesaCode }),
            }));
        await db.SaveChangesAsync(ct);
        await edit.Transaction.CommitAsync(ct);

        await events.Changed(ticket, ticket.TableId, ct);
        await ApprovalExpiry.Publish(posEvents, expired, ct);
        return new SettleResult(ticket.Id, ticket.ReceiptNumber.Value, ticket.AmountDue, change, false);
    }
}

/// <summary>
/// "Close ticket": a ticket whose every line was voided or cancelled, with nothing to pay and no
/// payments, closes with no receipt, no receipt number and nothing posted, when
/// <c>tickets.closeVoidedWithoutReceipt</c> is on (otherwise it is settled with a 0 receipt). Needs
/// <c>ticket.settle</c> and the ticket open on this terminal; no shift is needed, as no money moves.
/// </summary>
public sealed class CloseVoidedTicket(
    ITausiDb db, TicketEditing editing, IPermissionService permissions, IApprovalTokenSource approval,
    IAuditLog audit, IPosEvents posEvents, TicketEvents events, TimeProvider clock)
{
    public async Task Execute(Guid ticketId, CancellationToken ct)
    {
        await using var edit = await editing.Open(ticketId, ct);
        var grant = await permissions.Require(edit.User, PermissionKeys.TicketSettle, approval.ApprovalToken, ct);
        if (!(await PaymentSettings.Load(db, ct)).CloseVoidedWithoutReceipt)
            throw new ConflictException("Closing without a receipt is switched off. Settle it with a 0 receipt.", SettleCodes.CloseVoidedOff);
        var ticket = edit.Ticket;
        if (!IsFullyVoided(ticket))
            throw new ConflictException($"#{ticket.Number} still has items to pay for. Settle it instead.", SettleCodes.NotFullyVoided);
        if (ticket.Payments.Any(p => p.Counts))
            throw new ConflictException($"#{ticket.Number} has payments. Remove them first.", SettleCodes.NotFullyVoided);
        await SettleChecks.EnsureNoPendingRequests(db, ticket.Id, ct);

        var now = clock.GetUtcNow();
        ticket.Status = TicketStatus.Cancelled;
        ticket.ClearLock();
        ticket.Touch(now);
        var expired = await ApprovalExpiry.ForTickets(db, [ticket.Id], ApprovalExpiry.TicketClosed, now, ct);
        audit.Write(new AuditRecord(AuditActions.TicketClosedVoided, nameof(Ticket), ticket.Id.ToString(),
            new { ticket = ticket.Number, status = TicketStatus.Open.ToString() },
            new { status = TicketStatus.Cancelled.ToString(), lines = ticket.Lines.Count }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await edit.Transaction.CommitAsync(ct);
        await events.Changed(ticket, ticket.TableId, ct);
        await ApprovalExpiry.Publish(posEvents, expired, ct);
    }

    /// <summary>Every line voided or cancelled (at least one) and nothing to pay.</summary>
    public static bool IsFullyVoided(Ticket ticket) =>
        ticket.Lines.Count > 0 && ticket.Lines.All(l => !l.Counts) && ticket.Total == 0;
}

/// <summary>
/// The sale's journal entry: debit what came in (Cash for the cash applied, not tendered; M-Pesa;
/// Card clearing), credit Sales net of VAT after discounts (one line per VAT rate) and VAT
/// payable per rate, and the rounding either way, so it balances to the cent. Cost of sales and
/// stock are added by Task 11 in <see cref="CostOfSales"/>.
/// </summary>
public static class SalePosting
{
    public const string SourceType = "Sale";

    public static JournalEntryDraft? Draft(Ticket ticket, IReadOnlyList<Payment> payments, Guid workPeriodId, DateTimeOffset now)
    {
        var memo = $"Receipt {ticket.ReceiptNumber} · Ticket #{ticket.Number}";
        var lines = new List<JournalLineDraft>();
        void Debit(string account, decimal amount, string text) { if (amount > 0) lines.Add(JournalLineDraft.Dr(account, amount, text)); }
        void Credit(string account, decimal amount, string text) { if (amount > 0) lines.Add(JournalLineDraft.Cr(account, amount, text)); }

        Debit(LedgerAccountCodes.Cash, payments.Where(p => p.Type == PaymentType.Cash).Sum(p => p.Amount), "Cash");
        Debit(LedgerAccountCodes.MPesa, payments.Where(p => p.Type == PaymentType.Mpesa).Sum(p => p.Amount), "M-Pesa");
        Debit(LedgerAccountCodes.CardClearing, payments.Where(p => p.Type == PaymentType.Card).Sum(p => p.Amount), "Card");

        // Sales and VAT per rate (exempt items apart), from the amounts stored on each line.
        foreach (var rate in ticket.Lines.Where(l => l.Counts).GroupBy(l => (l.VatRate, l.VatExempt)).OrderByDescending(g => g.Key.VatRate))
        {
            var label = rate.Key.VatExempt ? "VAT exempt" : $"VAT {rate.Key.VatRate:0.##}%";
            Credit(LedgerAccountCodes.Sales, rate.Sum(l => l.Net), $"Sales · {label}");
            Credit(LedgerAccountCodes.VatPayable, rate.Sum(l => l.Vat), label);
        }

        // Rounding up (customer paid more) is income; rounding down is a cost.
        Credit(LedgerAccountCodes.Rounding, ticket.RoundingAmount, "Rounding");
        Debit(LedgerAccountCodes.Rounding, -ticket.RoundingAmount, "Rounding");

        CostOfSales(ticket, lines);
        if (lines.Count == 0) return null; // Nothing to post (everything was gifted).
        return new JournalEntryDraft(now, memo, SourceType, ticket.Id.ToString(), lines, workPeriodId);
    }

    /// <summary>Task 11: debit Cost of sales and credit Stock from the recipes. Nothing yet.</summary>
    private static void CostOfSales(Ticket ticket, List<JournalLineDraft> lines)
    {
    }
}

/// <summary>Who may have brought the money: staff logged in now or who worked this day, the ticket's waiter first.</summary>
public sealed class BroughtByChoices(ITausiDb db, ICurrentSession session, TimeProvider clock)
{
    public async Task<IReadOnlyList<BroughtByDto>> Execute(Guid ticketId, CancellationToken ct)
    {
        session.RequireUser();
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(k => k.Id == ticketId, ct)
            ?? throw new NotFoundException("Ticket not found.");
        var now = clock.GetUtcNow();
        var day = await WorkPeriods.Current(db, ct);
        var since = day?.OpenedAt ?? now.AddHours(-12);
        var sessions = await db.UserSessions.AsNoTracking()
            .Where(s => (s.EndedAt == null && s.ExpiresAt > now) || s.StartedAt >= since)
            .Select(s => s.UserId).Distinct().ToListAsync(ct);
        var ids = sessions.Append(ticket.OpenedByUserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Include(u => u.Role).Where(u => ids.Contains(u.Id)).ToListAsync(ct);
        return users
            .Where(u => u.Active || u.Id == ticket.OpenedByUserId)
            .OrderByDescending(u => u.Id == ticket.OpenedByUserId).ThenBy(u => u.Name)
            .Select(u => new BroughtByDto(u.Id, u.Name, u.Role?.Name ?? "", u.Id == ticket.OpenedByUserId))
            .ToList();
    }
}

/// <summary>The tickets settled in the current work period, newest first (needs <c>ticket.settle</c>).</summary>
public sealed class GetSettledTickets(ITausiDb db, ICurrentSession session, IPermissionService permissions)
{
    public async Task<IReadOnlyList<SettledTicketDto>> Execute(CancellationToken ct)
    {
        var user = session.RequireUser();
        if (await permissions.Check(user, PermissionKeys.TicketSettle, ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(PermissionKeys.TicketSettle);
        var day = await WorkPeriods.Current(db, ct);
        if (day is null) return [];
        var settled = await db.Tickets.AsNoTracking().Include(k => k.Table).Include(k => k.Payments).Include(k => k.Lines)
            .Where(k => k.Status == TicketStatus.Settled && k.WorkPeriodId == day.Id).ToListAsync(ct);
        var cashierIds = settled.Select(k => k.SettledByUserId).OfType<Guid>().Distinct().ToList();
        var cashiers = await db.Users.AsNoTracking().Where(u => cashierIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return settled
            .OrderByDescending(k => k.ReceiptNumber)
            .Select(k => new SettledTicketDto(k.Id, k.ReceiptNumber ?? 0, k.Number, TicketPrints.Where(k), k.AmountDue,
                k.Payments.Where(p => p.Status == PaymentStatus.Final).Select(p => p.Type.ToString()).Distinct().ToList(),
                k.SettledByUserId is { } c ? cashiers.GetValueOrDefault(c, "?") : "?", k.SettledAt ?? k.LastActivityAt))
            .ToList();
    }
}

/// <summary>The receipt again, marked COPY, on this terminal's receipt printer. Needs <c>ticket.reprint</c>; audited.</summary>
public sealed class ReprintReceipt(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    TicketPrints prints, IAuditLog audit)
{
    public async Task Execute(Guid ticketId, CancellationToken ct)
    {
        var user = session.RequireUser();
        if (user.TerminalId is not { } terminalId)
            throw new ForbiddenException("Receipts are printed from a terminal.", "terminal_required");
        var grant = await permissions.Require(user, PermissionKeys.TicketReprint, approval.ApprovalToken, ct);
        var ticket = await db.Tickets.AsNoTracking().Include(k => k.Lines).Include(k => k.Table).Include(k => k.Payments)
            .FirstOrDefaultAsync(k => k.Id == ticketId, ct) ?? throw new NotFoundException("Ticket not found.");
        if (ticket.Status != TicketStatus.Settled) throw new DomainException($"#{ticket.Number} isn't settled yet.");
        var job = await prints.Receipt(ticket, terminalId, copy: true, ct)
            ?? throw new DomainException("This terminal has no receipt printer. Set one in the admin site under Terminals and printers.", PrintCodes.NoPrinter);
        audit.Write(new AuditRecord(AuditActions.ReceiptReprinted, nameof(Ticket), ticket.Id.ToString(), null,
            new { ticket = ticket.Number, receipt = ticket.ReceiptNumber, printJobId = job.Id }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Settling needs the cashier's open shift on this terminal (its drawer), not yet counted.</summary>
public static class SettleShift
{
    public static async Task<Domain.Cash.CashierShift> Require(Cash.ShiftReader shifts, Guid userId, Guid terminalId, CancellationToken ct)
    {
        var shift = await shifts.OpenFor(userId, terminalId, ct)
            ?? throw new ConflictException("Start your shift first: settling needs your shift on this terminal's drawer.", Cash.ShiftCodes.ShiftRequired);
        if (shift.CountedAt is not null)
            throw new ConflictException("Your cash is counted already. Close the shift, then start a new one to settle.", Cash.ShiftCodes.AlreadyCounted);
        return shift;
    }
}
