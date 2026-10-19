using System.Text.Json;
using FluentAssertions;
using Nexagen.Application.Settings;
using Nexagen.Domain.Common;

namespace Nexagen.Tests.Auth;

public class PosSettingsTests
{
    [Theory]
    [InlineData("{}", 10, false)]
    [InlineData("""{"pos":{"idleLogoutMinutes":1,"logoutAfterSettle":true}}""", 1, true)]
    [InlineData("""{"pos":{"idleLogoutMinutes":0}}""", 1, false)]
    [InlineData("not json", 10, false)]
    [InlineData(null, 10, false)]
    public void Reads_with_defaults(string? json, int idle, bool afterSettle)
    {
        PosSettings.From(json).Should().Be(new PosSettings(idle, afterSettle));
    }

    [Fact]
    public void Default_json_round_trips()
    {
        PosSettings.From(PosSettings.DefaultJson).Should().Be(PosSettings.Default);
    }

    [Theory]
    [InlineData("""{"pos":{"idleLogoutMinutes":0}}""")]
    [InlineData("""{"pos":{"idleLogoutMinutes":"ten"}}""")]
    [InlineData("""{"pos":{"logoutAfterSettle":"yes"}}""")]
    [InlineData("""{"pos":[]}""")]
    public void Validation_refuses_bad_values(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var act = () => PosSettings.Validate(doc.RootElement);
        act.Should().Throw<DomainException>();
    }
}
