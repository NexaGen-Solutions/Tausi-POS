using Microsoft.EntityFrameworkCore;
using Nexagen.Domain.Settings;

namespace Nexagen.Infrastructure.Persistence;

/// <summary>PostgreSQL (Linux and development). Migrations in Migrations/Postgres.</summary>
public sealed class PostgresNexagenDbContext(DbContextOptions<PostgresNexagenDbContext> options)
    : NexagenDbContext(options)
{
    protected override string? ActiveUserFilter => "\"Active\"";
    protected override string? OpenWorkPeriodFilter => "\"IsOpen\"";
    protected override string? DefaultPriceListFilter => "\"IsDefault\"";

    protected override void OnModelCreating(ModelBuilder m)
    {
        base.OnModelCreating(m);
        m.Entity<BusinessSettings>().Property(x => x.OtherJson).HasColumnType("jsonb");
    }
}

/// <summary>SQL Server Express (Windows customers). Migrations in Migrations/SqlServer.</summary>
public sealed class SqlServerNexagenDbContext(DbContextOptions<SqlServerNexagenDbContext> options)
    : NexagenDbContext(options)
{
    protected override string? ActiveUserFilter => "[Active] = 1";
    protected override string? OpenWorkPeriodFilter => "[IsOpen] = 1";
    protected override string? DefaultPriceListFilter => "[IsDefault] = 1";
}
