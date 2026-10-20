using System.Text.Json;
using FluentAssertions;
using Tausi.Application.Printing;
using Tausi.Application.Settings;
using Tausi.Domain.Common;

namespace Tausi.Tests.Printing;

public class PoweredByFooterTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("{}", false)]
    [InlineData("""{"pos":{"idleLogoutMinutes":10}}""", false)]
    [InlineData("""{"print":{"poweredByFooter":false}}""", false)]
    [InlineData("""{"print":{"poweredByFooter":true}}""", true)]
    [InlineData("not json", false)]
    public void It_is_off_unless_switched_on(string? json, bool expected)
    {
        PoweredByFooter.IsOn(json).Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"print":{"poweredByFooter":"yes"}}""")]
    [InlineData("""{"print":{"poweredByFooter":1}}""")]
    public void Only_true_or_false_is_accepted(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var act = () => PoweredByFooter.Validate(doc.RootElement);
        act.Should().Throw<DomainException>().WithMessage("print.poweredByFooter must be true or false.");
    }

    [Fact]
    public void The_line_is_printed_centred_only_when_on()
    {
        PoweredByFooter.AddTo(new ReceiptBuilder(32).Text("Thank you"), on: false).Build().Text
            .Should().NotContain("Tausi");

        var lines = PoweredByFooter.AddTo(new ReceiptBuilder(32).Text("Thank you"), on: true).Build().Text
            .Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        lines.Should().Contain("      Powered by Tausi POS");
    }
}
