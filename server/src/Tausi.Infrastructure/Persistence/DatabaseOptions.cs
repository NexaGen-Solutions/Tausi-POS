namespace Tausi.Infrastructure.Persistence;

public enum DatabaseProvider
{
    Postgres,
    SqlServer,
}

/// <summary>Config section "Database". The connection string is "ConnectionStrings:Tausi".</summary>
public sealed class DatabaseOptions
{
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Postgres;
}
