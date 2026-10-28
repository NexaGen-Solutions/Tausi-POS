using FluentAssertions;
using Tausi.Api.Platform;

namespace Tausi.Tests.Platform;

/// <summary>Commands that exist to develop and test Tausi POS are refused on an installed server (Production).</summary>
public class DevelopmentOnlyCommandsTests
{
    [Theory]
    [InlineData("seed --demo")]
    [InlineData("seed --demo-menu")]
    [InlineData("require-pin-change edna")]
    [InlineData("close-day")]
    [InlineData("reset --confirm")]
    public void Are_refused_outside_development(string command)
    {
        var args = command.Split(' ');
        Commands.RefusedOutsideDevelopment(args, development: false).Should().Contain("development only");
        Commands.RefusedOutsideDevelopment(args, development: true).Should().BeNull();
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("migrate")]
    [InlineData("backup")]
    [InlineData("reset-admin")]
    [InlineData("configure")]
    [InlineData("check")]
    public void Commands_an_installed_server_needs_are_never_refused(string command)
    {
        Commands.RefusedOutsideDevelopment(command.Split(' '), development: false).Should().BeNull();
    }
}
