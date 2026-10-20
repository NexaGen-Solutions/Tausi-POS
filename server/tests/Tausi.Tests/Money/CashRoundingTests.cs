using FluentAssertions;
using Tausi.Domain.Money;

namespace Tausi.Tests.Money;

public class CashRoundingTests
{
    [Theory]
    [InlineData(1369.50, RoundingRule.NearestOne, 1370)]
    [InlineData(1369.49, RoundingRule.NearestOne, 1369)]
    [InlineData(1370.00, RoundingRule.NearestOne, 1370)]
    [InlineData(1372.50, RoundingRule.NearestFive, 1375)]
    [InlineData(1372.49, RoundingRule.NearestFive, 1370)]
    [InlineData(1365.00, RoundingRule.NearestTen, 1370)]
    [InlineData(1364.99, RoundingRule.NearestTen, 1360)]
    [InlineData(1369.37, RoundingRule.None, 1369.37)]
    [InlineData(-2.50, RoundingRule.NearestOne, -3)]
    public void Rounds_the_total_half_away_from_zero(decimal total, RoundingRule rule, decimal expected)
    {
        CashRounding.Apply(total, rule).Rounded.Should().Be(expected);
    }

    [Fact]
    public void Difference_is_what_gets_stored_as_a_rounding_line()
    {
        var result = CashRounding.Apply(1369.50m, RoundingRule.NearestOne);

        result.Difference.Should().Be(0.50m);
        (result.Exact + result.Difference).Should().Be(result.Rounded);
    }

    [Fact]
    public void Rounding_down_gives_a_negative_difference()
    {
        CashRounding.Apply(1369.49m, RoundingRule.NearestOne).Difference.Should().Be(-0.49m);
    }
}
