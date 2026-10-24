using Microsoft.EntityFrameworkCore;
using Tausi.Application.Stock;

namespace Tausi.Infrastructure.Persistence;

public sealed class StockDatabase(TausiDbContext db) : IStockDatabase
{
    public async Task LockItems(IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        if (itemIds.Count == 0) return;
        // Always in the same order, so two deliveries of overlapping items cannot deadlock.
        var ids = itemIds.Distinct().Order().Cast<object>().ToArray();
        var list = string.Join(", ", ids.Select((_, i) => $"{{{i}}}"));
        var provider = db.Database.ProviderName ?? "";
        string? sql = null;
        if (provider.Contains("Npgsql", StringComparison.Ordinal))
            sql = "SELECT \"Id\" FROM \"StockItems\" WHERE \"Id\" IN (" + list + ") ORDER BY \"Id\" FOR UPDATE";
        else if (provider.Contains("SqlServer", StringComparison.Ordinal))
            sql = "SELECT [Id] FROM [StockItems] WITH (UPDLOCK, ROWLOCK) WHERE [Id] IN (" + list + ")";
        // SQLite has no row locks: a write transaction already holds the whole database.
        if (sql is not null) await db.Database.ExecuteSqlRawAsync(sql, ids, ct);
    }
}
