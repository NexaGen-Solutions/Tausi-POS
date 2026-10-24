using FluentAssertions;
using Tausi.Application.Settings;

namespace Tausi.Tests.Printing;

public class PrintSettingsTests
{
    [Theory]
    [InlineData(null, true, false, false)]
    [InlineData("{}", true, false, false)]
    [InlineData("""{"pos":{"idleLogoutMinutes":10}}""", true, false, false)]
    [InlineData("""{"print":{"logoOnBill":false}}""", false, false, false)]
    [InlineData("""{"print":{"poweredByFooter":true}}""", true, true, false)]
    [InlineData("""{"print":{"poweredByFooter":"yes","showVoidsOnReceipt":true}}""", true, false, true)]
    [InlineData("not json", true, false, false)]
    public void Reads_with_defaults_logo_on_powered_by_off_voids_off(string? json, bool logo, bool poweredBy, bool voids)
    {
        PrintSettings.From(json).Should().Be(new PrintSettings(logo, poweredBy, voids));
    }
}
