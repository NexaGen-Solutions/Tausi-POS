using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tausi.Infrastructure.Persistence;

// Used only by `dotnet ef migrations add`; no database connection is made.

public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresTausiDbContext>
{
    public PostgresTausiDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresTausiDbContext>()
            .UseNpgsql("Host=localhost;Database=nexagen_design")
            .Options);
}

public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<SqlServerTausiDbContext>
{
    public SqlServerTausiDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqlServerTausiDbContext>()
            .UseSqlServer("Server=localhost;Database=nexagen_design;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
}
