using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tausi.Infrastructure.Seeding;

namespace Tausi.Infrastructure.Persistence;

/// <summary>Brings the database up to date: migrations, then the base seed and the ledger backfill.</summary>
public static class DatabaseMigrator
{
    public static async Task Migrate(IServiceScopeFactory scopes, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TausiDbContext>();
        await db.Database.MigrateAsync(ct);
        await SqlServerLogSetting.Apply(db, scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseMigrator)), ct);
        await scope.ServiceProvider.GetRequiredService<Seeder>().SeedBase(ct);
        await scope.ServiceProvider.GetRequiredService<Application.Money.LedgerBackfill>().Run(ct);
    }
}
