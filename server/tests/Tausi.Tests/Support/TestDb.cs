using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Tausi.Infrastructure.Persistence;

namespace Tausi.Tests.Support;

/// <summary>SQLite stores DateTimeOffset as text, which cannot be compared in queries; store it as a number instead.</summary>
public sealed class SqliteTestDbContext(DbContextOptions options) : TausiDbContext(options)
{
    protected override string? ActiveUserFilter => "\"Active\" = 1";
    protected override string? OpenWorkPeriodFilter => "\"IsOpen\" = 1";
    protected override string? DefaultPriceListFilter => "\"IsDefault\" = 1";
    protected override string? OpenShiftFilter => "\"Status\" = 'Open'";

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        base.ConfigureConventions(b);
        b.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        b.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }
}

/// <summary>An in-memory SQLite database with the real model, for unit tests of use cases and services.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public TestDb()
    {
        _connection.Open();
        Context = NewContext();
        Context.Database.EnsureCreated();
    }

    public SqliteTestDbContext Context { get; }

    /// <summary>A second context on the same database, e.g. to read back what was saved.</summary>
    public SqliteTestDbContext NewContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<SqliteTestDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
