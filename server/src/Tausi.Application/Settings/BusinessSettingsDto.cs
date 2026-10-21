using Tausi.Domain.Money;
using Tausi.Domain.Settings;

namespace Tausi.Application.Settings;

public sealed record BusinessSettingsDto(
    string Name,
    Guid? LogoFileId,
    string Address,
    string Phone,
    decimal VatRate,
    string Currency,
    RoundingRule Rounding,
    string ReceiptFooter,
    PrintMode PrintMode,
    string OtherJson,
    long RowVersion,
    string TimeZone)
{
    /// <summary>An empty time zone (from before the setting) shows as the server's.</summary>
    public static BusinessSettingsDto From(BusinessSettings s) => new(
        s.Name, s.LogoFileId, s.Address, s.Phone, s.VatRate, s.Currency, s.Rounding,
        s.ReceiptFooter, s.PrintMode, s.OtherJson, s.RowVersion,
        string.IsNullOrWhiteSpace(s.TimeZone) ? BusinessTimeZone.ServerZoneId() : s.TimeZone);
}

/// <summary>The public face of the business, shown before login and in the admin sidebar.</summary>
public sealed record BrandDto(string Name, Guid? LogoFileId, string Currency);
