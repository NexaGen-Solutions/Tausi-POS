using FluentAssertions;
using Tausi.Application.Settings;

namespace Tausi.Tests.Auth;

public class PosSettingsTests
{
    [Theory]
    [InlineData("{}", 10, false)]
    [InlineData("""{"pos":{"idleLogoutMinutes":1,"logoutAfterSettle":true}}""", 1, true)]
    [InlineData("""{"pos":{"idleLogoutMinutes":0}}""", 10, false)]
    [InlineData("""{"pos":{"idleLogoutMinutes":"ten"}}""", 10, false)]
    [InlineData("not json", 10, false)]
    [InlineData(null, 10, false)]
    public void Reads_with_defaults_and_a_stored_value_that_is_no_longer_valid_falls_back(string? json, int idle, bool afterSettle)
    {
        PosSettings.From(json).Should().Be(new PosSettings(idle, afterSettle));
    }

    [Fact]
    public void With_idle_logout_off_a_session_never_goes_idle()
    {
        var settings = PosSettings.From("""{"pos":{"idleLogoutEnabled":false,"idleLogoutMinutes":5}}""");

        settings.IdleLogoutMinutesIfOn.Should().BeNull();
        settings.IdleLimit.Should().Be(TimeSpan.MaxValue);
        PosSettings.From("""{"pos":{"idleLogoutMinutes":5}}""").IdleLimit.Should().Be(TimeSpan.FromMinutes(5));
    }
}
