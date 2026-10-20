using Tausi.Domain.Common;

namespace Tausi.Domain.Devices;

public class Terminal : VersionedEntity
{
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the device token the terminal sends on every request. Null until paired.</summary>
    public string? DeviceTokenHash { get; set; }

    /// <summary>Switched off in the admin site: nobody can log in on it. It stays paired.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Set when an admin revoked the device token; cleared when it is paired again.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? DispensePrinterId { get; set; }
    public Printer? DispensePrinter { get; set; }
    /// <summary>
    /// Printed at the top of this terminal's dispense slips, e.g. "Counter A" (so Pillar 1's slips
    /// say where to pour). Null uses the dispense printer's header, then the terminal's name.
    /// </summary>
    public string? DispenseHeader { get; set; }

    /// <summary>Bills and final receipts.</summary>
    public Guid? ReceiptPrinterId { get; set; }
    public Printer? ReceiptPrinter { get; set; }

    /// <summary>Use this price list instead of the default; null uses the default.</summary>
    public Guid? PriceListId { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
    public string? AppVersion { get; set; }

    /// <summary>Printer names installed on this PC, as the terminal app last reported them (JSON array).</summary>
    public string LocalPrintersJson { get; set; } = "[]";

    public bool IsPaired => DeviceTokenHash is not null;

    public string EffectiveDispenseHeader => DispenseHeader ?? DispensePrinter?.DispenseHeader ?? Name;

    /// <summary>Set when too many wrong PINs were typed here.</summary>
    public DateTimeOffset? PinLockedUntil { get; set; }
}
