using Tausi.Domain.Common;

namespace Tausi.Domain.Money;

/// <summary>Amounts stored on each order line. Gross includes VAT; Net = Gross - Vat.</summary>
public readonly record struct LineAmounts(decimal Gross, decimal Net, decimal Vat)
{
    public static readonly LineAmounts Zero = new(0m, 0m, 0m);

    public static LineAmounts operator +(LineAmounts a, LineAmounts b) =>
        new(a.Gross + b.Gross, a.Net + b.Net, a.Vat + b.Vat);
}

/// <summary>
/// Menu prices include VAT. For each line VAT = gross x rate / (100 + rate), rounded half away from
/// zero to 2 decimals. Ticket totals are sums of lines, never recalculated from the total.
/// </summary>
public static class VatCalculator
{
    public static decimal VatIncluded(decimal gross, decimal ratePercent)
    {
        EnsureValidRate(ratePercent);
        return ratePercent == 0m ? 0m : MoneyMath.Round(gross * ratePercent / (100m + ratePercent));
    }

    /// <param name="unitPrice">Price for one, including VAT.</param>
    /// <param name="quantity">May be fractional (e.g. half a portion) or negative (a reversal).</param>
    /// <param name="ratePercent">The item's VAT rate, e.g. 16.</param>
    /// <param name="exempt">Exempt items carry no VAT whatever the rate.</param>
    public static LineAmounts ForLine(decimal unitPrice, decimal quantity, decimal ratePercent, bool exempt = false)
    {
        var gross = MoneyMath.Round(unitPrice * quantity);
        var vat = exempt ? 0m : VatIncluded(gross, ratePercent);
        return new LineAmounts(gross, gross - vat, vat);
    }

    public static LineAmounts Sum(IEnumerable<LineAmounts> lines) =>
        lines.Aggregate(LineAmounts.Zero, (total, line) => total + line);

    private static void EnsureValidRate(decimal ratePercent)
    {
        if (ratePercent is < 0m or >= 100m)
            throw new DomainException("A VAT rate must be from 0 to below 100 percent.");
    }
}
