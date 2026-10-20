using System.Text.Json;
using FluentAssertions;
using Tausi.Application.Settings;
using Tausi.Domain.Common;

namespace Tausi.Tests.Printing;

public class PrintSettingsTests
{
    [Theory]
    [InlineData(null, true, false)]
    [InlineData("{}", true, false)]
    [InlineData("""{"pos":{"idleLogoutMinutes":10}}""", true, false)]
    [InlineData("""{"print":{"logoOnBill":false}}""", false, false)]
    [InlineData("""{"print":{"poweredByFooter":true}}""", true, true)]
    [InlineData("not json", true, false)]
    public void Reads_with_defaults_logo_on_and_powered_by_off(string? json, bool logo, bool poweredBy)
    {
        PrintSettings.From(json).Should().Be(new PrintSettings(logo, poweredBy));
    }

    [Fact]
    public void A_new_business_starts_with_the_defaults_written_out()
    {
        PrintSettings.From(PosSettings.DefaultJson).Should().Be(PrintSettings.Default);
        PosSettings.DefaultJson.Should().Contain("\"poweredByFooter\":false");
    }

    [Theory]
    [InlineData("""{"print":{"poweredByFooter":"yes"}}""", "print.poweredByFooter must be true or false.")]
    [InlineData("""{"print":{"poweredByFooter":1}}""", "print.poweredByFooter must be true or false.")]
    [InlineData("""{"print":{"logoOnBill":"no"}}""", "print.logoOnBill must be true or false.")]
    [InlineData("""{"print":[]}""", "\"print\" settings must be a JSON object.")]
    public void Only_true_or_false_is_accepted(string json, string message)
    {
        using var doc = JsonDocument.Parse(json);
        var act = () => PrintSettings.Validate(doc.RootElement);
        act.Should().Throw<DomainException>().WithMessage(message);
    }
}
