using FluentAssertions;
using Nexagen.Domain.Common;
using M = Nexagen.Domain.Money.Money;

namespace Nexagen.Tests.Money;

public class MoneyTests
{
    [Fact]
    public void Adds_and_subtracts_in_the_same_currency()
    {
        (new M(250m, "KES") + new M(120.50m, "KES") - new M(70m, "KES")).Should().Be(new M(300.50m, "KES"));
    }

    [Fact]
    public void Refuses_to_mix_currencies()
    {
        var act = () => new M(1m, "KES") + new M(1m, "USD");
        act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(2.345, 2.35)]
    [InlineData(-2.345, -2.35)]
    [InlineData(2.344, 2.34)]
    public void Rounds_half_away_from_zero(decimal amount, decimal expected)
    {
        new M(amount, "KES").Round().Amount.Should().Be(expected);
    }

    [Fact]
    public void Multiplies_by_a_quantity()
    {
        (new M(250m, "KES") * 3).Should().Be(new M(750m, "KES"));
    }
}
