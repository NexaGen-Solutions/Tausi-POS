using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Day;
using Tausi.Application.Settings;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.Mpesa;
using Tausi.Domain.Service;

namespace Tausi.Application.Mpesa;

/// <summary>What an M-Pesa payment did to a bill.</summary>
public sealed record AppliedPayment(Guid PaymentId, Guid TicketId, long TicketNumber, decimal Amount);

/// <summary>
/// Adds an M-Pesa payment to a bill as a pending payment, without the cashier's edit lock (a prompt
/// answered, a QR payment matched, the ledger's Apply to ticket). It locks the ticket's row, uses at
/// most what the bill still owes and what the transaction has left, and never gives change. The
/// cashier still presses Complete. The payment is taken by whoever settles; until then by
/// <paramref name="takenBy"/>, with no cashier shift.
/// </summary>
public sealed class MpesaApply(ITausiDb db, ITicketDatabase tickets, IAuditLog audit, TicketEvents events, TimeProvider clock)
{
    /// <summary>The ticket's waiter: who brings an M-Pesa payment unless a waiter sent the prompt.</summary>
    public static Guid BroughtBy(Ticket ticket, Guid? waiter) => waiter ?? ticket.OpenedByUserId;

    /// <summary>
    /// Applies <paramref name="transaction"/> (tracked by this context) to the ticket, or does nothing
    /// and returns null when the ticket is closed or owes nothing, or the day isn't open.
    /// Call inside a transaction the caller commits; publishes nothing.
    /// </summary>
    public async Task<AppliedPayment?> ToTicket(
        MpesaTransaction transaction, Guid ticketId, decimal? wanted, Guid takenBy, Guid? broughtBy, CancellationToken ct)
    {
        await tickets.LockRows([ticketId], ct);
        var ticket = await db.Tickets.Include(k => k.Lines).Include(k => k.Payments).FirstOrDefaultAsync(k => k.Id == ticketId, ct);
        if (ticket is not { Status: TicketStatus.Open }) return null;
        var day = await WorkPeriods.Current(db, ct);
        if (day is null) return null;
        ticket.ApplyRounding(await MoneySettings.Rounding(db, ct));
        var remaining = ticket.Remaining;
        if (remaining <= 0 || transaction.Available <= 0) return null;
        var amount = transaction.Apply(Math.Min(wanted ?? remaining, remaining));

        var now = clock.GetUtcNow();
        var payment = new Payment
        {
            TicketId = ticket.Id, Type = PaymentType.Mpesa, Amount = amount, MpesaCode = transaction.CodeKnown ? transaction.TransactionId : null,
            MpesaTransactionId = transaction.Id, TakenByUserId = takenBy, BroughtByUserId = BroughtBy(ticket, broughtBy),
            TerminalId = ticket.TerminalId, WorkPeriodId = day.Id, TakenAt = now,
        };
        ticket.Payments.Add(payment);
        db.Payments.Add(payment);
        ticket.Touch(now);
        audit.Write(new AuditRecord(AuditActions.PaymentAdded, nameof(Ticket), ticket.Id.ToString(), null,
            new
            {
                ticket = ticket.Number, type = PaymentType.Mpesa.ToString(), amount, payment.MpesaCode, source = transaction.Source.ToString(),
                broughtBy = payment.BroughtByUserId,
            }, UserId: takenBy, TerminalId: ticket.TerminalId));
        return new AppliedPayment(payment.Id, ticket.Id, ticket.Number, amount);
    }

    /// <summary>Publishes the change on the ticket (after the caller committed).</summary>
    public async Task Changed(Guid ticketId, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(k => k.Id == ticketId, ct);
        if (ticket is not null) await events.Changed(ticket, ticket.TableId, ct);
    }

    /// <summary>A ticket number in a reference: "1042", "#1042", " 1042 ".</summary>
    public static long? TicketNumberOf(string? reference)
    {
        var text = reference?.Trim().TrimStart('#').Trim();
        return long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : null;
    }

    public static void EnsureNotTraining(bool training)
    {
        if (training) throw new DomainException("M-Pesa prompts and received payments aren't used in training mode. Type a code instead.", MpesaCodes.Unavailable);
    }
}
