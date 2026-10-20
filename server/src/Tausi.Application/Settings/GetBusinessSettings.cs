using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Domain.People;

namespace Tausi.Application.Settings;

public sealed class GetBusinessSettings(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval)
{
    public async Task<BusinessSettingsDto> Execute(CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);
        var s = await db.BusinessSettings.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("The business has not been set up yet.");
        return BusinessSettingsDto.From(s);
    }
}
