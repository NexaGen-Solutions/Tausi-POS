using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nexagen.Infrastructure.Persistence;

// Used only by `dotnet ef migrations add`; no database connection is made.

public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresNexagenDbContext>
{
    public PostgresNexagenDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresNexagenDbContext>()
            .UseNpgsql("Host=localhost;Database=nexagen_design")
            .Options);
}

public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<SqlServerNexagenDbContext>
{
    public SqlServerNexagenDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqlServerNexagenDbContext>()
            .UseSqlServer("Server=localhost;Database=nexagen_design;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
}
