using Tausi.Domain.Common;
using Tausi.Domain.Money;

namespace Tausi.Domain.Settings;

public enum PrintMode
{
    OneButton,
    SendAndBill,
}

/// <summary>The business's own details. There is exactly one row.</summary>
public class BusinessSettings : VersionedEntity
{
    public string Name { get; set; } = "";
    public Guid? LogoFileId { get; set; }

    /// <summary>The logo resized for screens (the PIN screen and admin site); <see cref="LogoFileId"/> is the original.</summary>
    public Guid? LogoWebFileId { get; set; }
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public decimal VatRate { get; set; } = 16m;
    public string Currency { get; set; } = "KES";
    public RoundingRule Rounding { get; set; } = RoundingRule.NearestOne;
    public string ReceiptFooter { get; set; } = "";
    public PrintMode PrintMode { get; set; } = PrintMode.OneButton;

    /// <summary>
    /// The business's time zone (IANA name, e.g. "Africa/Nairobi"), for every printed time and date.
    /// A new row takes the server's time zone; empty (rows from before the setting) also means the server's.
    /// </summary>
    public string TimeZone { get; set; } = BusinessTimeZone.ServerZoneId();

    /// <summary>Settings that do not need their own column yet, as a JSON object.</summary>
    public string OtherJson { get; set; } = "{}";
}
