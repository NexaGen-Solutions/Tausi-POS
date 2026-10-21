using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Day;
using Tausi.Application.Security;

namespace Tausi.Application.Terminals;

public sealed record DeviceInfo(
    Guid TerminalId,
    string TerminalName,
    string BusinessName,
    Guid? LogoFileId,
    WorkPeriodDto? WorkPeriod);

/// <summary>What a paired terminal shows before anyone logs in (wireframe 1, left panel).</summary>
public sealed class GetDeviceInfo(ITausiDb db, ICurrentSession session)
{
    public async Task<DeviceInfo> Execute(CancellationToken ct)
    {
        var terminal = session.RequireTerminal();
        var business = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        return new DeviceInfo(
            terminal.TerminalId, terminal.Name, business?.Name ?? "", business?.LogoFileId,
            await WorkPeriods.Current(db, ct));
    }
}
