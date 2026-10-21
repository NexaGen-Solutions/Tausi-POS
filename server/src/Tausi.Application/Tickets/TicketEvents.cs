using Tausi.Application.Events;
using Tausi.Domain.Service;

namespace Tausi.Application.Tickets;

/// <summary>
/// Counts changes to the table map. Every <c>ticket.changed</c>, <c>ticket.lock</c> and
/// <c>tables.changed</c> carries the next number, and <c>GET /api/pos/tables</c> the current one, so a
/// terminal that sees a gap knows it missed an event and refetches. It restarts with the server,
/// which also shows up as a gap.
/// </summary>
public sealed class TableMapVersion
{
    private long _value;

    public long Current => Interlocked.Read(ref _value);

    public long Next() => Interlocked.Increment(ref _value);
}

/// <summary>Publishes the table map's live events.</summary>
public sealed class TicketEvents(IPosEvents events, TableMapVersion version)
{
    public IPosEvents Events => events;

    public Task Changed(Ticket ticket, Guid? tableId, CancellationToken ct) =>
        events.Publish(PosEventNames.TicketChanged,
            new { ticketId = ticket.Id, tableId, status = ticket.Status.ToString(), version = version.Next() }, ct);

    public Task Lock(Guid ticketId, Guid? tableId, LockHolder? holder, CancellationToken ct) =>
        events.Publish(PosEventNames.TicketLock,
            new
            {
                ticketId,
                tableId,
                terminalId = holder?.TerminalId,
                terminal = holder?.TerminalName,
                user = holder?.UserName,
                version = version.Next(),
            }, ct);

    public Task TablesChanged(CancellationToken ct) =>
        events.Publish(PosEventNames.TablesChanged, new { version = version.Next() }, ct);
}
