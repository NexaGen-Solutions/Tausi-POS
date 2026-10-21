using FluentAssertions;
using Tausi.Application.Settings;
using Tausi.Domain.Settings;

namespace Tausi.Tests.Settings;

public class BusinessTimeZoneTests
{
    [Fact]
    public void New_settings_take_the_servers_time_zone_as_an_iana_name()
    {
        var zone = new BusinessSettings().TimeZone;

        zone.Should().Be(BusinessTimeZone.ServerZoneId());
        BusinessTimeZone.IsValid(zone).Should().BeTrue();
    }

    [Theory]
    [InlineData("Africa/Nairobi", true)]
    [InlineData("UTC", true)]
    [InlineData("Mars/Olympus", false)]
    [InlineData("E. Africa Standard Time", false)]
    [InlineData("", false)]
    public void Only_iana_names_are_accepted(string name, bool valid) =>
        BusinessTimeZone.IsValid(name).Should().Be(valid);

    [Fact]
    public void An_empty_setting_from_before_the_setting_means_the_servers_zone() =>
        BusinessTimeZone.Find("").Should().Be(TimeZoneInfo.Local);

    [Fact]
    public void Nairobi_is_three_hours_ahead_of_utc()
    {
        var utc = new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);

        BusinessClock.ToLocal(utc, BusinessTimeZone.Find("Africa/Nairobi")).ToString("HH:mm").Should().Be("21:00");
    }
}
