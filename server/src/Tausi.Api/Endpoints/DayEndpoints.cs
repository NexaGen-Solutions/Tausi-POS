using Tausi.Application.Day;

namespace Tausi.Api.Endpoints;

public static class DayEndpoints
{
    public static void MapDayEndpoints(this WebApplication app)
    {
        var day = app.MapGroup("/api/day").WithTags("Day").RequireAuthorization();

        // Needs day.open_close. The current day is part of GET /api/device.
        day.MapPost("/open", (OpenDay open, CancellationToken ct) => open.Execute(ct));
    }
}
