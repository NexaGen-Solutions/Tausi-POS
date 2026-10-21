using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Service;

namespace Tausi.Application.Tickets;

/// <summary>A ticket being changed: its row is locked until the transaction ends.</summary>
public sealed record EditableTicket(Ticket Ticket, SessionUser User, Guid TerminalId, IDbContextTransaction Transaction) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Transaction.DisposeAsync();
}

/// <summary>
/// Every change to a ticket goes through here: a transaction with the ticket row locked (rule 8),
/// the ticket still open, and this terminal holding its edit lock (Task 5), so two terminals never
/// change one ticket at the same time.
/// </summary>
public sealed class TicketEditing(ITausiDb db, ICurrentSession session, ITicketDatabase tickets, TicketLocks locks)
{
    public const string NotLockedCode = "ticket_not_locked";

    public async Task<EditableTicket> Open(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        if (user.TerminalId is not { } terminalId)
            throw new ForbiddenException("Tickets are changed on a terminal.", "terminal_required");

        var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await tickets.LockRows([id], ct);
            var ticket = await db.Tickets.Include(k => k.Lines).Include(k => k.Table).Include(k => k.Payments)
                .FirstOrDefaultAsync(k => k.Id == id, ct)
                ?? throw new NotFoundException("Ticket not found.");
            if (!ticket.IsOpen) throw new DomainException($"#{ticket.Number} is no longer open.");

            var holder = await locks.Holder(ticket, ct);
            if (holder is null)
                throw new ConflictException("This ticket isn't open on this terminal any more. Open it again from the tables.", NotLockedCode);
            if (holder.TerminalId != terminalId)
                throw new ConflictException(holder.ReadOnlyMessage, TicketCodes.Locked);
            return new EditableTicket(ticket, user, terminalId, tx);
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
    }
}
