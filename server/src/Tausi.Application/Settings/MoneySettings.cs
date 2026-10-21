using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.Money;

namespace Tausi.Application.Settings;

/// <summary>The rounding rule for totals due (BusinessSettings.Rounding; nearest shilling by default).</summary>
public static class MoneySettings
{
    public static async Task<RoundingRule> Rounding(ITausiDb db, CancellationToken ct) =>
        await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).Select(s => (RoundingRule?)s.Rounding).FirstOrDefaultAsync(ct)
            ?? RoundingRule.NearestOne;
}
