using Tausi.Application.Cash;
using Tausi.Application.Day;

namespace Tausi.Api.Endpoints;

public static class DayEndpoints
{
    public static void MapDayEndpoints(this WebApplication app)
    {
        var day = app.MapGroup("/api/day").WithTags("Day").RequireAuthorization();

        // Needs day.open_close. The current day is part of GET /api/device.
        day.MapPost("/open", (OpenDay open, CancellationToken ct) => open.Execute(ct));

        var pos = app.MapGroup("/api/pos").WithTags("Day and shifts").RequireAuthorization();

        // Closing the day: what blocks it and the summary, then the close (day.open_close).
        pos.MapGet("/day", (GetDayStatus status, CancellationToken ct) => status.Execute(ct));
        pos.MapPost("/day/close", (CloseWorkPeriod close, CancellationToken ct) => close.Execute(ct));

        // Cashier shifts: open, count, close with a supervisor's sign-off (approval token), pay out and drop.
        pos.MapPost("/shifts", (OpenShiftRequest r, OpenShift open, CancellationToken ct) => open.Execute(r, ct));
        pos.MapGet("/shifts/current", (ShiftQueries shifts, CancellationToken ct) => shifts.Current(ct));
        pos.MapGet("/shifts/{id:guid}/summary", (Guid id, ShiftQueries shifts, CancellationToken ct) => shifts.Summary(id, ct));
        pos.MapPost("/shifts/{id:guid}/count", (Guid id, CountShiftRequest r, CountShift count, CancellationToken ct) => count.Execute(id, r, ct));
        pos.MapPost("/shifts/{id:guid}/close", (Guid id, CloseShift close, CancellationToken ct) => close.Execute(id, ct));
        pos.MapPost("/shifts/{id:guid}/pay-out", (Guid id, DrawerMovementRequest r, DrawerMovements moves, CancellationToken ct) =>
            moves.PayOut(id, r, ct));
        pos.MapPost("/shifts/{id:guid}/drop", (Guid id, DrawerMovementRequest r, DrawerMovements moves, CancellationToken ct) =>
            moves.Drop(id, r, ct));

        // My sales: only the logged-in user's own figures (reports.own).
        pos.MapGet("/my-sales", (GetMySales mySales, CancellationToken ct) => mySales.Execute(ct));
        pos.MapPost("/my-sales/print", async (PrintMySales print, CancellationToken ct) =>
        {
            await print.Execute(ct);
            return Results.NoContent();
        });
    }
}
