using Tausi.Application.Backups;
using Tausi.Api.Platform;

namespace Tausi.Api.Endpoints;

public static class BackupEndpoints
{
    public sealed record PasswordRequest(string? Password);

    public static void MapBackupEndpoints(this WebApplication app)
    {
        // The banner on the admin site and the Close day screen: anyone signed in.
        app.MapGet("/api/backups/status", (BackupStatus status) => status.Get()).WithTags("Backups").RequireAuthorization();

        var admin = app.MapGroup("/api/admin/backups").WithTags("Backups").RequireAuthorization();
        admin.MapGet("/", (BackupAdmin b, CancellationToken ct) => b.List(ct));
        admin.MapGet("/settings", (BackupAdmin b, CancellationToken ct) => b.Settings(ct));
        admin.MapPut("/settings", (SaveBackupSettingsRequest r, BackupAdmin b, CancellationToken ct) => b.SaveSettings(r, ct));
        admin.MapPost("/", (BackupAdmin b, CancellationToken ct) => b.RunNow(ct));
        admin.MapGet("/{name}/download", async (string name, BackupAdmin b, CancellationToken ct) =>
            Results.File(await b.Download(name, ct), "application/octet-stream", name, enableRangeProcessing: true));
        admin.MapPost("/{name}/inspect", (string name, PasswordRequest r, BackupAdmin b, CancellationToken ct) => b.Inspect(name, r.Password, ct))
            .ReadsOnly();
        admin.MapPost("/{name}/restore", async (string name, RestoreRequest r, BackupAdmin b, CancellationToken ct) =>
        {
            await b.Restore(r with { Name = name }, ct);
            return Results.NoContent();
        })
            // The restore replaces the table the idempotency store writes to; a repeat restores the same file again.
            .ReadsOnly();
    }
}
