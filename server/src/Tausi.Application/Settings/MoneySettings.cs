using Tausi.Application.Abstractions;
using Tausi.Domain.Money;

namespace Tausi.Application.Settings;

/// <summary>The rounding rule for totals due (<c>money.rounding</c>; nearest shilling by default).</summary>
public static class MoneySettings
{
    public static async Task<RoundingRule> Rounding(ITausiDb db, CancellationToken ct) =>
        (await SettingsRegistry.Load(db, ct)).Enum<RoundingRule>(SettingsRegistry.MoneyRounding);
}
