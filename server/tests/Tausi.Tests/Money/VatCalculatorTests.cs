using FluentAssertions;
using Tausi.Domain.Common;
using Tausi.Domain.Money;

namespace Tausi.Tests.Money;

public class VatCalculatorTests
{
    [Theory]
    [InlineData(250, 34.48, 215.52)]
    [InlineData(100, 13.79, 86.21)]
    [InlineData(300, 41.38, 258.62)]
    [InlineData(1370, 188.97, 1181.03)]
    [InlineData(1, 0.14, 0.86)]
    [InlineData(0.01, 0.00, 0.01)]
    public void Price_includes_VAT_at_16_percent(decimal gross, decimal vat, decimal net)
    {
        var line = VatCalculator.ForLine(gross, 1, 16m);

        line.Should().Be(new LineAmounts(gross, net, vat));
    }

    [Fact]
    public void Gross_is_always_net_plus_vat()
    {
        for (var cents = 1; cents <= 20_000; cents += 7)
        {
            var line = VatCalculator.ForLine(cents / 100m, 1, 16m);
            (line.Net + line.Vat).Should().Be(line.Gross);
        }
    }

    [Fact]
    public void Zero_rate_has_no_vat()
    {
        VatCalculator.ForLine(250m, 2, 0m).Should().Be(new LineAmounts(500m, 500m, 0m));
    }

    [Fact]
    public void Exempt_item_has_no_vat_whatever_the_rate()
    {
        VatCalculator.ForLine(100m, 1, 16m, exempt: true).Should().Be(new LineAmounts(100m, 100m, 0m));
    }

    [Fact]
    public void Quantity_times_price_is_rounded_half_away_from_zero_before_vat()
    {
        // 33.345 is a midpoint: half away from zero gives 33.35 (banker's rounding would give 33.34).
        var line = VatCalculator.ForLine(33.345m, 1, 16m);

        line.Gross.Should().Be(33.35m);
        line.Vat.Should().Be(4.60m);
        line.Net.Should().Be(28.75m);
    }

    [Theory]
    [InlineData(0.12, 0.05)]   // 0.12 x 60/160 = 0.045, a midpoint: rounds up, not to even (0.04)
    [InlineData(-0.12, -0.05)] // and away from zero for negatives
    [InlineData(0.04, 0.02)]   // 0.015 -> 0.02
    public void Vat_midpoints_round_half_away_from_zero(decimal gross, decimal expectedVat)
    {
        VatCalculator.VatIncluded(gross, 60m).Should().Be(expectedVat);
    }

    [Fact]
    public void Quantity_multiplies_the_unit_price()
    {
        VatCalculator.ForLine(250m, 3, 16m).Should().Be(new LineAmounts(750m, 646.55m, 103.45m));
    }

    [Fact]
    public void Fractional_quantity_is_allowed()
    {
        VatCalculator.ForLine(250m, 0.5m, 16m).Should().Be(new LineAmounts(125m, 107.76m, 17.24m));
    }

    [Fact]
    public void Negative_quantity_reverses_a_line_exactly()
    {
        var sale = VatCalculator.ForLine(270m, 1, 16m);
        var reversal = VatCalculator.ForLine(270m, -1, 16m);

        (sale + reversal).Should().Be(LineAmounts.Zero);
    }

    [Fact]
    public void Ticket_totals_are_sums_of_lines()
    {
        // The ticket in wireframe 3: 2 x 250, 300, 100, 2 x 100, 270.
        var lines = new[]
        {
            VatCalculator.ForLine(250m, 2, 16m),
            VatCalculator.ForLine(300m, 1, 16m),
            VatCalculator.ForLine(100m, 1, 16m),
            VatCalculator.ForLine(100m, 2, 16m),
            VatCalculator.ForLine(270m, 1, 16m),
        };

        var total = VatCalculator.Sum(lines);

        total.Gross.Should().Be(1370m);
        total.Vat.Should().Be(68.97m + 41.38m + 13.79m + 27.59m + 37.24m);
        total.Net.Should().Be(total.Gross - total.Vat);
    }

    [Fact]
    public void Line_sums_can_differ_from_vat_on_the_total_and_lines_win()
    {
        // Three separate 250 lines: 3 x 34.48 = 103.44, while VAT on 750 would be 103.45.
        var lines = Enumerable.Repeat(VatCalculator.ForLine(250m, 1, 16m), 3);

        VatCalculator.Sum(lines).Vat.Should().Be(103.44m);
        VatCalculator.VatIncluded(750m, 16m).Should().Be(103.45m);
    }

    [Fact]
    public void Mixed_rates_and_exempt_lines_sum_per_line()
    {
        var total = VatCalculator.Sum(
        [
            VatCalculator.ForLine(250m, 1, 16m),
            VatCalculator.ForLine(100m, 1, 8m),
            VatCalculator.ForLine(80m, 1, 16m, exempt: true),
        ]);

        total.Should().Be(new LineAmounts(430m, 430m - 34.48m - 7.41m, 34.48m + 7.41m));
    }

    [Fact]
    public void Empty_ticket_sums_to_zero()
    {
        VatCalculator.Sum([]).Should().Be(LineAmounts.Zero);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(150)]
    public void Invalid_rates_are_refused(decimal rate)
    {
        var act = () => VatCalculator.ForLine(100m, 1, rate);
        act.Should().Throw<DomainException>();
    }
}
