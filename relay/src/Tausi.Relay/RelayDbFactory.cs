using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tausi.Relay;

/// <summary>For <c>dotnet ef migrations add</c> only (no database is opened).</summary>
public sealed class RelayDbFactory : IDesignTimeDbContextFactory<RelayDb>
{
    public RelayDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RelayDb>().UseNpgsql("Host=localhost;Database=design_only").Options);
}
