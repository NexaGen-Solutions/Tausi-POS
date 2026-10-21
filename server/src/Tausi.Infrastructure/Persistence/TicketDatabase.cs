using Microsoft.EntityFrameworkCore;
using Tausi.Application.Tickets;

namespace Tausi.Infrastructure.Persistence;

/// <summary>Ticket numbers and row locks, in each database's own SQL.</summary>
public sealed class TicketDatabase(TausiDbContext db) : ITicketDatabase
{
    private string Provider => db.Database.ProviderName ?? "";

    public async Task<long> NextReceiptNumber(CancellationToken ct)
    {
        if (await FromSequence(TausiDbContext.ReceiptNumberSequence, ct) is { } next) return next;
        // SQLite (unit tests only), as for ticket numbers.
        var last = await db.Tickets.MaxAsync(t => t.ReceiptNumber, ct);
        var pending = db.ChangeTracker.Entries<Domain.Service.Ticket>().Select(e => e.Entity.ReceiptNumber ?? 0).DefaultIfEmpty(0).Max();
        return Math.Max(Math.Max(last ?? TausiDbContext.FirstReceiptNumber - 1, pending) + 1, TausiDbContext.FirstReceiptNumber);
    }

    private async Task<long?> FromSequence(string sequence, CancellationToken ct)
    {
        // The sequence names are our own constants, never input.
        string? sql = null;
        if (Provider.Contains("Npgsql", StringComparison.Ordinal)) sql = "SELECT nextval('\"" + sequence + "\"') AS \"Value\"";
        else if (Provider.Contains("SqlServer", StringComparison.Ordinal)) sql = "SELECT NEXT VALUE FOR [" + sequence + "] AS [Value]";
        if (sql is null) return null;
        return (await db.Database.SqlQueryRaw<long>(sql).ToListAsync(ct)).Single();
    }

    public async Task<long> NextNumber(CancellationToken ct)
    {
        // Run as written: composing (e.g. SingleAsync) would wrap it in a subquery, where SQL Server
        // does not allow NEXT VALUE FOR.
        if (Provider.Contains("Npgsql", StringComparison.Ordinal))
            return (await db.Database
                .SqlQueryRaw<long>($"SELECT nextval('\"{TausiDbContext.TicketNumberSequence}\"') AS \"Value\"")
                .ToListAsync(ct)).Single();
        if (Provider.Contains("SqlServer", StringComparison.Ordinal))
            return (await db.Database
                .SqlQueryRaw<long>($"SELECT NEXT VALUE FOR [{TausiDbContext.TicketNumberSequence}] AS [Value]")
                .ToListAsync(ct)).Single();

        // SQLite (unit tests only): no sequences, and one writer at a time anyway.
        var last = await db.Tickets.MaxAsync(t => (long?)t.Number, ct);
        var pending = db.ChangeTracker.Entries<Domain.Service.Ticket>().Select(e => e.Entity.Number).DefaultIfEmpty(0).Max();
        return Math.Max(Math.Max(last ?? TausiDbContext.FirstTicketNumber - 1, pending) + 1, TausiDbContext.FirstTicketNumber);
    }

    public async Task LockRows(IReadOnlyCollection<Guid> ticketIds, CancellationToken ct)
    {
        if (ticketIds.Count == 0) return;
        // Always in the same order, so two operations on overlapping tickets cannot deadlock.
        var ids = ticketIds.Distinct().Order().Cast<object>().ToArray();
        // Only the parameter placeholders {0}, {1}, ... go into the text; the ids are passed as parameters.
        var list = string.Join(", ", ids.Select((_, i) => $"{{{i}}}"));

        string? sql = null;
        if (Provider.Contains("Npgsql", StringComparison.Ordinal))
            sql = "SELECT \"Id\" FROM \"Tickets\" WHERE \"Id\" IN (" + list + ") ORDER BY \"Id\" FOR UPDATE";
        else if (Provider.Contains("SqlServer", StringComparison.Ordinal))
            sql = "SELECT [Id] FROM [Tickets] WITH (UPDLOCK, ROWLOCK) WHERE [Id] IN (" + list + ")";
        // SQLite has no row locks: a write transaction already holds the whole database.
        if (sql is not null) await db.Database.ExecuteSqlRawAsync(sql, ids, ct);
    }
}
