using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Day;

public sealed record WorkPeriodDto(Guid Id, DateTimeOffset OpenedAt, Guid OpenedByUserId);

/// <summary>
/// Opens the business day (a work period). Needs <c>day.open_close</c>. Only one day can be open;
/// a unique index makes sure of it even if two terminals try at once.
/// </summary>
public sealed class OpenDay(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public async Task<WorkPeriodDto> Execute(CancellationToken ct)
    {
        var user = session.RequireUser();
        var grant = await permissions.Require(user, PermissionKeys.DayOpenClose, approval.ApprovalToken, ct);

        if (await db.WorkPeriods.AnyAsync(w => w.IsOpen, ct))
            throw new ConflictException("The day is already open.");

        var period = new WorkPeriod
        {
            OpenedAt = clock.GetUtcNow(),
            OpenedByUserId = user.UserId,
            OpenedTerminalId = user.TerminalId ?? session.Terminal?.TerminalId,
        };
        db.WorkPeriods.Add(period);
        audit.Write(new AuditRecord(
            AuditActions.DayOpened, nameof(WorkPeriod), period.Id.ToString(),
            NewValue: new { period.OpenedAt }, ApprovedByUserId: grant.ApprovedByUserId));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            throw new ConflictException("The day is already open.");
        }

        await events.Publish(PosEventNames.DayOpened, new { workPeriodId = period.Id, openedAt = period.OpenedAt }, ct);
        return new WorkPeriodDto(period.Id, period.OpenedAt, period.OpenedByUserId);
    }
}

public static class WorkPeriods
{
    public static Task<WorkPeriodDto?> Current(ITausiDb db, CancellationToken ct) =>
        db.WorkPeriods.AsNoTracking()
            .Where(w => w.IsOpen)
            .Select(w => new WorkPeriodDto(w.Id, w.OpenedAt, w.OpenedByUserId))
            .FirstOrDefaultAsync(ct);
}
