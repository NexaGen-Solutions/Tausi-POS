namespace Tausi.Domain.Service;

/// <summary>Spreads a discount over amounts in proportion, to the cent, adding up exactly.</summary>
public static class DiscountSpread
{
    /// <returns>One share per amount, in the same order. Their sum is exactly <paramref name="discount"/>.</returns>
    public static IReadOnlyList<decimal> Spread(decimal discount, IReadOnlyList<decimal> amounts)
    {
        var shares = new decimal[amounts.Count];
        var basis = amounts.Sum();
        if (discount <= 0 || basis <= 0) return shares;
        if (discount > basis) discount = basis;

        for (var i = 0; i < amounts.Count; i++)
            shares[i] = Money.MoneyMath.Round(amounts[i] * discount / basis);

        // The rounding leftover (a few cents either way) goes on the largest line.
        var largest = 0;
        for (var i = 1; i < amounts.Count; i++)
            if (amounts[i] > amounts[largest]) largest = i;
        shares[largest] += discount - shares.Sum();
        return shares;
    }
}
