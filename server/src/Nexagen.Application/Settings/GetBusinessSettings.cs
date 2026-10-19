using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Common;
using Nexagen.Application.Security;
using Nexagen.Domain.People;

namespace Nexagen.Application.Settings;

public sealed class GetBusinessSettings(
    INexagenDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval)
{
    public async Task<BusinessSettingsDto> Execute(CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);
        var s = await db.BusinessSettings.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("The business has not been set up yet.");
        return BusinessSettingsDto.From(s);
    }
}
