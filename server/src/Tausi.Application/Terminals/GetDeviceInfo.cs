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
    WorkPeriodDto? WorkPeriod,
    DevicePrinting? Printing = null);

/// <summary>
/// Where this terminal prints, for the order screen ("…then a slip with the 2 new items to
/// Dispense A") and which print buttons it shows (<c>OneButton</c> or <c>SendAndBill</c>).
/// <see cref="DispenseName"/> is the dispense printer's template ("Dispense A") or its name.
/// </summary>
public sealed record DevicePrinting(string Mode, string? ReceiptPrinter, string? DispensePrinter, string? DispenseName);

/// <summary>What a paired terminal shows before anyone logs in (wireframe 1, left panel).</summary>
public sealed class GetDeviceInfo(ITausiDb db, ICurrentSession session)
{
    public async Task<DeviceInfo> Execute(CancellationToken ct)
    {
        var terminal = session.RequireTerminal();
        var business = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        var printers = await db.Terminals.AsNoTracking().Where(t => t.Id == terminal.TerminalId)
            .Select(t => new
            {
                Receipt = t.ReceiptPrinter!.Name,
                Dispense = t.DispensePrinter!.Name,
                Template = t.DispensePrinter!.DispenseTemplate,
            })
            .FirstOrDefaultAsync(ct);
        return new DeviceInfo(
            terminal.TerminalId, terminal.Name, business?.Name ?? "", business?.LogoFileId,
            await WorkPeriods.Current(db, ct),
            new DevicePrinting((business?.PrintMode ?? Domain.Settings.PrintMode.OneButton).ToString(),
                printers?.Receipt, printers?.Dispense, printers?.Template ?? printers?.Dispense));
    }
}
