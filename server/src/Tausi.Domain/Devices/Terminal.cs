using Tausi.Domain.Common;

namespace Tausi.Domain.Devices;

public class Terminal : VersionedEntity
{
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the device token the terminal sends on every request. Null until paired.</summary>
    public string? DeviceTokenHash { get; set; }

    public Guid? DispensePrinterId { get; set; }
    public Printer? DispensePrinter { get; set; }
    public Guid? ReceiptPrinterId { get; set; }
    public Printer? ReceiptPrinter { get; set; }

    /// <summary>Use this price list instead of the default; null uses the default.</summary>
    public Guid? PriceListId { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
    public string? AppVersion { get; set; }

    /// <summary>Set when too many wrong PINs were typed here.</summary>
    public DateTimeOffset? PinLockedUntil { get; set; }
}
