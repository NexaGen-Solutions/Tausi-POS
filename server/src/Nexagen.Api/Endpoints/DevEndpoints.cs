using Nexagen.Application.Dev;

namespace Nexagen.Api.Endpoints;

public static class DevEndpoints
{
    /// <summary>
    /// TEMPORARY test-only endpoints, mapped only in Development and Testing.
    /// /api/dev/void-probe is guarded by order.void to exercise the approval flow; remove it with the void task.
    /// </summary>
    public static void MapDevEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) return;

        var dev = app.MapGroup("/api/dev").WithTags("Dev only").RequireAuthorization();
        dev.MapPost("/void-probe", (DevVoidProbe probe, CancellationToken ct) => probe.Execute(ct));
    }
}
