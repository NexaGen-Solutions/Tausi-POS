using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.Mpesa;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Mpesa;

public sealed record SendPromptRequest(string Phone, decimal? Amount, bool Replace = false, bool FromOrderScreen = false);

/// <summary>A prompt as the screens show it (board 23).</summary>
public sealed record PromptDto(
    Guid Id, Guid TicketId, string Phone, decimal Amount, string Status, DateTimeOffset SentAt, DateTimeOffset ExpiresAt, string? Message,
    string? Code, string? PayerName, string? PayerPhone, DateTimeOffset? PaidAt, Guid? PaymentId, Guid? BroughtByUserId, string? BroughtBy);

/// <summary>
/// STK prompts (boards 22, 23 and 25). The cashier sends one from the settle screen (needs
/// <c>ticket.settle</c>); a waiter from the order screen when <c>mpesa.waiterPrompts</c> is on (needs
/// <c>order.add</c> and the ticket). One prompt waits per ticket; it expires after 2 minutes. Its
/// result arrives through the relay or, while the screen asks, through Daraja's query API every 5
/// seconds: whichever comes first. Every prompt is audited.
/// </summary>
public sealed class MpesaPrompts(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, TicketAccess access, ISecretProtector secrets, IDaraja daraja,
    MpesaPromptAnswers answers, IAuditLog audit, TimeProvider clock)
{
    public static readonly TimeSpan QueryEvery = TimeSpan.FromSeconds(5);

    public async Task<PromptDto> Send(Guid ticketId, SendPromptRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        MpesaApply.EnsureNotTraining(session.Terminal?.Training == true);
        var settings = await MpesaSettings.Load(db, secrets, ct);
        if (!settings.SetUp || !settings.StkEnabled)
            throw new DomainException("Prompts to phones aren't set up. Use Payments received or type the code.", MpesaCodes.NotSetUp);
        if (r.FromOrderScreen)
        {
            if (!settings.WaiterPrompts) throw new ForbiddenException("Waiters can't send M-Pesa prompts. Ask the cashier.", "mpesa_waiter_prompts_off");
            await permissions.Require(user, PermissionKeys.OrderAdd, ct: ct);
        }
        else if (await permissions.Check(user, PermissionKeys.TicketSettle, ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(PermissionKeys.TicketSettle);

        var phone = MpesaPhones.Normalise(r.Phone)
            ?? throw new FieldErrorsException(new Dictionary<string, string> { ["phone"] = "Type a Safaricom number, e.g. 0712 345 678." });
        var ticket = await db.Tickets.Include(k => k.Lines).Include(k => k.Payments).FirstOrDefaultAsync(k => k.Id == ticketId, ct)
            ?? throw new NotFoundException("Ticket not found.");
        if (!ticket.IsOpen) throw new DomainException($"#{ticket.Number} is no longer open.");
        if (r.FromOrderScreen) await access.EnsureMayChange(user, ticket, ct);
        ticket.ApplyRounding(await MoneySettings.Rounding(db, ct));
        var remaining = ticket.Remaining;
        if (remaining <= 0) throw new DomainException("Nothing is left to pay.");
        var amount = r.Amount ?? Math.Floor(remaining);
        if (amount <= 0 || amount != decimal.Truncate(amount))
            throw new FieldErrorsException(new Dictionary<string, string> { ["amount"] = "M-Pesa takes whole shillings." });
        if (amount > remaining)
            throw new FieldErrorsException(new Dictionary<string, string> { ["amount"] = $"It can't be more than the {remaining:#,##0.##} remaining." });

        var now = clock.GetUtcNow();
        var waiting = await db.MpesaPrompts.Where(x => x.TicketId == ticketId && x.Status == MpesaPromptStatus.Waiting && x.ExpiresAt > now)
            .ToListAsync(ct);
        if (waiting.Count > 0 && !r.Replace)
            throw new ConflictException("A prompt is still waiting for this bill. Cancel it and send a new one?", MpesaCodes.PromptWaiting);
        foreach (var old in waiting)
        {
            old.Status = MpesaPromptStatus.Cancelled;
            old.ResultMessage = "Replaced by a new prompt.";
            old.AnsweredAt = now;
        }

        StkPushResult pushed;
        try
        {
            pushed = await daraja.StkPush(settings.Credentials!, settings.Live, new StkPushRequest(
                settings.ShortCode, settings.PayTo, settings.Paybill, phone, (int)amount, settings.StkCallbackUrl,
                ticket.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"Bill {ticket.Number}"), ct);
        }
        catch (DarajaException e)
        {
            throw new DomainException($"M-Pesa couldn't send the prompt: {e.Message}", MpesaCodes.Unavailable);
        }

        var prompt = new MpesaPrompt
        {
            TicketId = ticket.Id, Phone = MpesaPhones.Mask(phone), Amount = amount, CheckoutRequestId = pushed.CheckoutRequestId,
            MerchantRequestId = pushed.MerchantRequestId, SentByUserId = user.UserId, TerminalId = user.TerminalId ?? ticket.TerminalId,
            FromOrderScreen = r.FromOrderScreen, SentAt = now, ExpiresAt = now + MpesaPrompt.Lifetime,
        };
        db.MpesaPrompts.Add(prompt);
        audit.Write(new AuditRecord(AuditActions.MpesaPromptSent, nameof(Ticket), ticket.Id.ToString(), null,
            new { ticket = ticket.Number, phone = prompt.Phone, amount, from = r.FromOrderScreen ? "order screen" : "settle screen", replaced = waiting.Count }));
        await db.SaveChangesAsync(ct);
        return await answers.Dto(prompt, ct);
    }

    /// <summary>
    /// The prompt now: when it still waits and the relay hasn't answered, asks Daraja (at most every 5
    /// seconds); after 2 minutes it has expired.
    /// </summary>
    public async Task<PromptDto> Status(Guid promptId, CancellationToken ct)
    {
        session.RequireUser();
        var prompt = await db.MpesaPrompts.FirstOrDefaultAsync(x => x.Id == promptId, ct) ?? throw new NotFoundException("Prompt not found.");
        var now = clock.GetUtcNow();
        if (prompt.Status == MpesaPromptStatus.Waiting && (prompt.LastQueriedAt ?? prompt.SentAt) + QueryEvery <= now)
        {
            prompt.LastQueriedAt = now;
            await db.SaveChangesAsync(ct);
            var settings = await MpesaSettings.Load(db, secrets, ct);
            if (settings.SetUp)
            {
                try
                {
                    var state = await daraja.StkQuery(settings.Credentials!, settings.Live, settings.ShortCode, prompt.CheckoutRequestId, ct);
                    if (state.ResultCode is { } code) await answers.Answer(prompt.CheckoutRequestId, code, state.ResultDesc, null, ct);
                }
                catch (DarajaException)
                {
                    // Daraja unreachable: the relay may still answer; try again in 5 seconds.
                }
            }
        }
        if (prompt.Status == MpesaPromptStatus.Waiting && now >= prompt.ExpiresAt)
        {
            prompt.Status = MpesaPromptStatus.Expired;
            prompt.ResultMessage = "No answer within 2 minutes. Send it again or take another payment.";
            prompt.AnsweredAt = now;
            await db.SaveChangesAsync(ct);
        }
        return await answers.Dto(prompt, ct);
    }

    /// <summary>The waiting prompt on a ticket, if any (the screen comes back to it).</summary>
    public async Task<PromptDto?> Current(Guid ticketId, CancellationToken ct)
    {
        session.RequireUser();
        var prompt = await db.MpesaPrompts.AsNoTracking().Where(x => x.TicketId == ticketId).OrderByDescending(x => x.SentAt).FirstOrDefaultAsync(ct);
        return prompt is null ? null : await answers.Dto(prompt, ct);
    }

    public async Task<PromptDto> Cancel(Guid promptId, CancellationToken ct)
    {
        var user = session.RequireUser();
        var prompt = await db.MpesaPrompts.FirstOrDefaultAsync(x => x.Id == promptId, ct) ?? throw new NotFoundException("Prompt not found.");
        if (prompt.Status == MpesaPromptStatus.Waiting)
        {
            prompt.Status = MpesaPromptStatus.Cancelled;
            prompt.ResultMessage = "Cancelled on the POS. If the customer still enters their PIN, the payment shows in Payments received.";
            prompt.AnsweredAt = clock.GetUtcNow();
            audit.Write(new AuditRecord(AuditActions.MpesaPromptCancelled, nameof(Ticket), prompt.TicketId.ToString(), null,
                new { prompt.Phone, prompt.Amount, by = user.Name }));
            await db.SaveChangesAsync(ct);
        }
        return await answers.Dto(prompt, ct);
    }
}

/// <summary>
/// An STK prompt's answer, from the relay's callback or Daraja's query, whichever is first; the
/// other is then ignored. Paid: a pending payment on the bill, brought by the waiter who sent the
/// prompt (or the ticket's waiter), and a confirmed transaction once the M-Pesa code is known (the
/// query doesn't give it; the callback fills it in).
/// </summary>
public sealed class MpesaPromptAnswers(ITausiDb db, MpesaApply apply, IAuditLog audit, IPosEvents events, TimeProvider clock, ILogger<MpesaPromptAnswers> log)
{
    public async Task<bool> FromCallback(RelayPaymentDto p, CancellationToken ct)
    {
        if (p.CheckoutRequestId is null) return true;
        var payer = (p.PayerName, p.Phone, p.PaidAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : (DateTimeOffset?)null);
        await Answer(p.CheckoutRequestId, p.ResultCode ?? -1, p.ResultDesc, (MpesaTransaction.CleanCode(p.TransactionId), p.Amount, payer), ct);
        return true;
    }

    /// <summary>Records the answer once. <paramref name="paid"/> carries the callback's code, amount and payer.</summary>
    public async Task Answer(string checkoutRequestId, int code, string? desc,
        (string? Code, decimal Amount, (string? Name, string? Phone, DateTimeOffset? At) Payer)? paid, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var prompt = await db.MpesaPrompts.FirstOrDefaultAsync(x => x.CheckoutRequestId == checkoutRequestId, ct);
        if (prompt is null)
        {
            log.LogWarning("An STK answer for an unknown prompt {Checkout}", checkoutRequestId);
            return;
        }
        var now = clock.GetUtcNow();
        AppliedPayment? applied = null;
        if (prompt.Status is MpesaPromptStatus.Waiting or MpesaPromptStatus.Expired or MpesaPromptStatus.Cancelled)
        {
            // Paid even after the screen gave up: the money is real, so it still counts.
            prompt.ResultCode = code;
            prompt.ResultMessage = StkResults.Message(code, desc);
            prompt.AnsweredAt = now;
            prompt.Status = code == StkResults.Paid ? MpesaPromptStatus.Paid : prompt.Status == MpesaPromptStatus.Waiting ? MpesaPromptStatus.Failed : prompt.Status;
            if (code == StkResults.Paid)
            {
                var amount = paid?.Amount > 0 ? paid.Value.Amount : prompt.Amount;
                var transaction = new MpesaTransaction
                {
                    TransactionId = paid?.Code ?? "", Amount = amount, Source = MpesaSource.Stk, Confirmed = true, ConfirmedAt = now,
                    PayerName = paid?.Payer.Name, Phone = paid?.Payer.Phone ?? prompt.Phone, At = paid?.Payer.At ?? now, ReceivedAt = now,
                    PromptId = prompt.Id, MatchedTicketId = prompt.TicketId,
                };
                if (transaction.TransactionId.Length == 0) transaction.TransactionId = Pending(prompt);
                db.MpesaTransactions.Add(transaction);
                var waiter = prompt.FromOrderScreen ? prompt.SentByUserId : (Guid?)null;
                applied = await apply.ToTicket(transaction, prompt.TicketId, amount, prompt.SentByUserId, waiter, ct);
                if (applied is not null) prompt.PaymentId = applied.PaymentId;
                prompt.TransactionId = transaction.Id;
            }
            audit.Write(new AuditRecord(AuditActions.MpesaPromptAnswered, nameof(Ticket), prompt.TicketId.ToString(), null,
                new { prompt.Phone, prompt.Amount, code, message = prompt.ResultMessage, mpesaCode = paid?.Code, applied = applied?.Amount }, Actor: "M-Pesa"));
        }
        else if (prompt.Status == MpesaPromptStatus.Paid && paid?.Code is { } mpesaCode && prompt.TransactionId is { } id)
        {
            // Paid through the query first: the callback brings the M-Pesa code and the payer.
            var transaction = await db.MpesaTransactions.FirstAsync(t => t.Id == id, ct);
            if (transaction.TransactionId == Pending(prompt))
            {
                transaction.TransactionId = mpesaCode;
                transaction.PayerName = paid.Value.Payer.Name;
                transaction.Phone = paid.Value.Payer.Phone ?? transaction.Phone;
                transaction.At = paid.Value.Payer.At ?? transaction.At;
                await db.Payments.Where(x => x.MpesaTransactionId == id).ForEachAsync(x => x.MpesaCode = mpesaCode, ct);
            }
        }
        else
        {
            return;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await events.Publish(PosEventNames.MpesaPrompt, new { promptId = prompt.Id, ticketId = prompt.TicketId, status = prompt.Status.ToString() }, ct);
        if (applied is not null) await apply.Changed(applied.TicketId, ct);
    }

    /// <summary>
    /// A transaction paid through the query has no M-Pesa code yet: a placeholder ("~" and 9
    /// characters of the prompt, never a real code) keeps the column unique until the callback brings it.
    /// </summary>
    public static string Pending(MpesaPrompt prompt) => MpesaTransaction.PlaceholderMark + prompt.Id.ToString("N")[^9..];

    public async Task<PromptDto> Dto(MpesaPrompt prompt, CancellationToken ct)
    {
        var transaction = prompt.TransactionId is { } t ? await db.MpesaTransactions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == t, ct) : null;
        var payment = prompt.PaymentId is { } p ? await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p, ct) : null;
        var broughtBy = payment is null ? null : await db.Users.AsNoTracking().Where(u => u.Id == payment.BroughtByUserId).Select(u => u.Name).FirstOrDefaultAsync(ct);
        var code = transaction?.TransactionId is { } c && c != Pending(prompt) ? c : null;
        return new PromptDto(prompt.Id, prompt.TicketId, prompt.Phone, prompt.Amount, prompt.Status.ToString(), prompt.SentAt, prompt.ExpiresAt,
            prompt.ResultMessage, code, transaction?.PayerName, transaction?.Phone, transaction?.At, payment?.Id, payment?.BroughtByUserId, broughtBy);
    }
}
