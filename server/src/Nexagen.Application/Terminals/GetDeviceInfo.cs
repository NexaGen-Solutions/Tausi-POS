using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Day;
using Nexagen.Application.Security;

namespace Nexagen.Application.Terminals;

public sealed record DeviceInfo(
    Guid TerminalId,
    string TerminalName,
    string BusinessName,
    Guid? LogoFileId,
    WorkPeriodDto? WorkPeriod);

/// <summary>What a paired terminal shows before anyone logs in (wireframe 1, left panel).</summary>
public sealed class GetDeviceInfo(INexagenDb db, ICurrentSession session)
{
    public async Task<DeviceInfo> Execute(CancellationToken ct)
    {
        var terminal = session.RequireTerminal();
        var business = await db.BusinessSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return new DeviceInfo(
            terminal.TerminalId, terminal.Name, business?.Name ?? "", business?.LogoFileId,
            await WorkPeriods.Current(db, ct));
    }
}
