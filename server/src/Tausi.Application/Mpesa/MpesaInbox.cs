using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Events;
using Tausi.Domain.Mpesa;
using Tausi.Domain.Service;

namespace Tausi.Application.Mpesa;

/// <summary>
/// Saves what the relay delivers, once per M-Pesa transaction id: an STK result answers its prompt
/// (and adds the payment to the bill), a C2B payment is kept for the cashier and, when its reference
/// is exactly an open ticket's number (a QR on that bill, or a Paybill account), added to that bill.
/// A typed code that arrives is linked and confirmed. Amount-only matches are never applied.
/// </summary>
public sealed class MpesaInbox(ITausiDb db, MpesaApply apply, MpesaPromptAnswers answers, IAuditLog audit, IPosEvents events,
    TimeProvider clock, ILogger<MpesaInbox> log)
{
    /// <summary>Saves one delivered payment; true when it is now in the database (new or already there).</summary>
    public async Task<bool> Receive(RelayPaymentDto p, CancellationToken ct)
    {
        if (p.Kind == "stk") return await answers.FromCallback(p, ct);
        if (p.Kind != "c2b" || MpesaTransaction.CleanCode(p.TransactionId) is not { } code) return true;

        var at = p.PaidAt is { } paid ? new DateTimeOffset(DateTime.SpecifyKind(paid, DateTimeKind.Utc)) : clock.GetUtcNow();
        var existing = await db.MpesaTransactions.FirstOrDefaultAsync(t => t.TransactionId == code, ct);
        if (existing is not null)
        {
            if (!existing.Confirmed) await Confirm(existing, p, ct);
            return true;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var transaction = new MpesaTransaction
        {
            TransactionId = code, Amount = p.Amount, PayerName = Cut(p.PayerName, MpesaTransaction.MaxNameLength),
            Phone = Cut(p.Phone, MpesaTransaction.MaxPhoneLength), At = at, ReceivedAt = clock.GetUtcNow(), ShortCode = Cut(p.ShortCode, 20),
            BillReference = Cut(p.BillRef, MpesaTransaction.MaxReferenceLength), Source = MpesaSource.C2B, Confirmed = true,
            ConfirmedAt = clock.GetUtcNow(),
        };
        db.MpesaTransactions.Add(transaction);
        await db.SaveChangesAsync(ct);

        AppliedPayment? applied = null;
        if (MpesaApply.TicketNumberOf(p.BillRef) is { } number &&
            await db.Tickets.AsNoTracking().Where(k => k.Number == number && k.Status == TicketStatus.Open).Select(k => (Guid?)k.Id)
                .FirstOrDefaultAsync(ct) is { } ticketId)
        {
            transaction.MatchedTicketId = ticketId;
            var opener = await db.Tickets.AsNoTracking().Where(k => k.Id == ticketId).Select(k => k.OpenedByUserId).FirstAsync(ct);
            applied = await apply.ToTicket(transaction, ticketId, null, opener, null, ct);
        }
        audit.Write(new AuditRecord(AuditActions.MpesaReceived, nameof(MpesaTransaction), transaction.Id.ToString(), null,
            new { code, p.Amount, reference = transaction.BillReference, ticket = applied?.TicketNumber, applied = applied?.Amount }, Actor: "M-Pesa"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        log.LogInformation("M-Pesa {Code} received{Applied}", code, applied is null ? "" : $", added to #{applied.TicketNumber}");

        await events.Publish(PosEventNames.MpesaReceived, new { id = transaction.Id, ticketId = applied?.TicketId }, ct);
        if (applied is not null) await apply.Changed(applied.TicketId, ct);
        return true;
    }

    /// <summary>A code a cashier typed, now confirmed by Safaricom's own message.</summary>
    private async Task Confirm(MpesaTransaction typed, RelayPaymentDto p, CancellationToken ct)
    {
        typed.Confirmed = true;
        typed.ConfirmedAt = clock.GetUtcNow();
        typed.PayerName ??= Cut(p.PayerName, MpesaTransaction.MaxNameLength);
        typed.Phone ??= Cut(p.Phone, MpesaTransaction.MaxPhoneLength);
        typed.ShortCode ??= Cut(p.ShortCode, 20);
        // Safaricom's amount is the truth: a bigger payment leaves the rest for another bill.
        var typedAmount = typed.Amount;
        typed.Amount = p.Amount;
        typed.Release(0);
        audit.Write(new AuditRecord(AuditActions.MpesaConfirmed, nameof(MpesaTransaction), typed.Id.ToString(),
            new { typed.TransactionId, amount = typedAmount, confirmed = false },
            new { typed.TransactionId, amount = p.Amount, confirmed = true, differs = p.Amount != typedAmount }, Actor: "M-Pesa"));
        await db.SaveChangesAsync(ct);
        await events.Publish(PosEventNames.MpesaReceived, new { id = typed.Id }, ct);
    }

    private static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}
