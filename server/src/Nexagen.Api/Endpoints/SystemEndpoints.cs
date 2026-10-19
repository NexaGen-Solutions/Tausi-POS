using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Nexagen.Infrastructure.Persistence;

namespace Nexagen.Api.Endpoints;

public static class SystemEndpoints
{
    public sealed record PrinterHealth(Guid Id, string Name, string Status, string? LastError);

    public sealed record Health(string Status, string Version, string Server, string Database, IReadOnlyList<PrinterHealth> Printers);

    public static readonly string Version =
        typeof(SystemEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", async (NexagenDbContext db, CancellationToken ct) =>
        {
            IReadOnlyList<PrinterHealth> printers = [];
            var database = "ok";
            try
            {
                printers = await db.Printers.AsNoTracking().OrderBy(p => p.Name)
                    .Select(p => new PrinterHealth(p.Id, p.Name, p.Status.ToString(), p.LastError))
                    .ToListAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                database = "unreachable";
            }
            return new Health(database == "ok" ? "ok" : "degraded", Version, Environment.MachineName, database, printers);
        }).WithTags("System").RequireCors(CorsSetup.TerminalApp);
    }
}
