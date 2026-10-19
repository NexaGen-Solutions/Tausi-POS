namespace Nexagen.Domain.Money;

public enum RoundingRule
{
    None,
    NearestOne,
    NearestFive,
    NearestTen,
}

/// <summary>The displayed/printed total and the difference, which is stored as its own line.</summary>
public readonly record struct RoundedTotal(decimal Exact, decimal Rounded)
{
    public decimal Difference => Rounded - Exact;
}

public static class CashRounding
{
    public static RoundedTotal Apply(decimal total, RoundingRule rule)
    {
        var step = rule switch
        {
            RoundingRule.NearestOne => 1m,
            RoundingRule.NearestFive => 5m,
            RoundingRule.NearestTen => 10m,
            _ => 0m,
        };
        var rounded = step == 0m
            ? total
            : Math.Round(total / step, 0, MidpointRounding.AwayFromZero) * step;
        return new RoundedTotal(total, rounded);
    }
}
