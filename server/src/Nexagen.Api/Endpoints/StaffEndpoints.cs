using Nexagen.Api.Platform;
using Nexagen.Application.Staff;

namespace Nexagen.Api.Endpoints;

public static class StaffEndpoints
{
    /// <summary>Staff and PINs (admin site). Everything needs staff.manage; who may manage whom is checked in the use cases.</summary>
    public static void MapStaffEndpoints(this WebApplication app)
    {
        var staff = app.MapGroup("/api/admin/staff").WithTags("Staff").RequireAuthorization();

        staff.MapGet("/", (ListStaff list, CancellationToken ct) => list.Execute(ct));
        staff.MapGet("/{id:guid}", (Guid id, GetStaffMember get, CancellationToken ct) => get.Execute(id, ct));

        // The temporary PIN is in these answers only; the idempotency store keeps a copy without it.
        staff.MapPost("/", (CreateStaffRequest request, CreateStaff create, CancellationToken ct) =>
            create.Execute(request, ct)).RedactOnReplay("temporaryPin");
        staff.MapPut("/{id:guid}", (Guid id, UpdateStaffRequest request, UpdateStaff update, CancellationToken ct) =>
            update.Execute(id, request, ct));
        staff.MapPost("/{id:guid}/reset-pin", (Guid id, SetPinRequest request, ResetStaffPin reset, CancellationToken ct) =>
            reset.Execute(id, request, ct)).RedactOnReplay("temporaryPin");
        staff.MapPost("/{id:guid}/deactivate", (Guid id, RowVersionRequest request, DeactivateStaff deactivate, CancellationToken ct) =>
            deactivate.Execute(id, request, ct));
        staff.MapPost("/{id:guid}/reactivate", (Guid id, SetPinRequest request, ReactivateStaff reactivate, CancellationToken ct) =>
            reactivate.Execute(id, request, ct)).RedactOnReplay("temporaryPin");
    }
}
