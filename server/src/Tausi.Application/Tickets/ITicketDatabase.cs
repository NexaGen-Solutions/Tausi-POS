namespace Tausi.Application.Tickets;

/// <summary>The two ticket operations whose SQL differs per database.</summary>
public interface ITicketDatabase
{
    /// <summary>The next ticket number from a database sequence: never repeated, even under load.</summary>
    Task<long> NextNumber(CancellationToken ct);

    /// <summary>
    /// Locks these ticket rows until the current transaction ends, so print, void, merge, move and
    /// settle on the same ticket happen one after the other (AGENTS.md rule 8).
    /// </summary>
    Task LockRows(IReadOnlyCollection<Guid> ticketIds, CancellationToken ct);
}
