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
    long RowVersion)
{
    public static BusinessSettingsDto From(BusinessSettings s) => new(
        s.Name, s.LogoFileId, s.Address, s.Phone, s.VatRate, s.Currency, s.Rounding,
        s.ReceiptFooter, s.PrintMode, s.OtherJson, s.RowVersion);
}

/// <summary>The public face of the business, shown before login and in the admin sidebar.</summary>
public sealed record BrandDto(string Name, Guid? LogoFileId, string Currency);
