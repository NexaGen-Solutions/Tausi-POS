using Nexagen.Domain.Common;

namespace Nexagen.Domain.Devices;

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

    public PrinterStatus Status { get; set; } = PrinterStatus.Unknown;
    public string? LastError { get; set; }

    /// <summary>Characters per line in the default font.</summary>
    public int Columns => PaperWidthMm == 58 ? 32 : 42;
}
