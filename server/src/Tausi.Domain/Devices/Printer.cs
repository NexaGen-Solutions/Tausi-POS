using Tausi.Domain.Common;

namespace Tausi.Domain.Devices;

public enum PrinterConnection
{
    Network9100,
    WindowsShare,
    ViaTerminal,

    /// <summary>Development: the job is written as text to the print-output folder.</summary>
    File,
}

public enum PrinterStatus
{
    Unknown,
    Online,
    Error,
}

public class Printer : VersionedEntity
{
    public string Name { get; set; } = "";
    public PrinterConnection ConnectionMode { get; set; }

    /// <summary>IP:port for Network9100, \\host\share for WindowsShare, the local printer name for ViaTerminal.</summary>
    public string Address { get; set; } = "";

    /// <summary>The terminal the printer is plugged into. Required for ViaTerminal.</summary>
    public Guid? OwningTerminalId { get; set; }
    public Terminal? OwningTerminal { get; set; }

    /// <summary>58 or 80 (mm).</summary>
    public int PaperWidthMm { get; set; } = 80;

    /// <summary>
    /// The dispense-slip template this printer uses, e.g. "Dispense A", or null if it prints no
    /// dispense slips. Templates are shared: Ticket Printer 1 and Pillar 1 both print "Dispense A".
    /// </summary>
    public string? DispenseTemplate { get; set; }

    /// <summary>The header printed on that template's slips, e.g. "Counter A".</summary>
    public string? DispenseHeader { get; set; }

    public PrinterStatus Status { get; set; } = PrinterStatus.Unknown;
    public string? LastError { get; set; }

    /// <summary>Characters per line in the default font.</summary>
    public int Columns => PaperWidthMm == 58 ? 32 : 42;
}
