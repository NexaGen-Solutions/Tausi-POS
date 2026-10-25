using FluentAssertions;
using Tausi.Application.Auth;
using Tausi.Application.Security;
using Tausi.Tests.Tickets;

namespace Tausi.Tests.Security;

/// <summary>Admin sign-in: 10 wrong passwords in 10 minutes lock the user name, and the address they came from.</summary>
public class LoginLimitsTests : ApprovalsFixtureBase
{
    private Task<SessionUser> Login(string user, string password, string ip) =>
        F.Get<AdminLogin>().Execute(new AdminLoginRequest(user, password), None, ip);

    [Fact]
    public async Task Ten_wrong_passwords_lock_the_user_name_for_ten_minutes()
    {
        for (var i = 0; i < 9; i++)
            await FluentActions.Awaiting(() => Login("admin", "wrong", $"10.0.0.{i}")).Should().ThrowAsync<LoginRejectedException>();
        var locked = await FluentActions.Awaiting(() => Login("admin", "wrong", "10.0.0.99")).Should().ThrowAsync<LoginLockedException>();
        locked.Which.Until.Should().Be(F.Clock.GetUtcNow() + LoginThrottle.Window);
        await FluentActions.Awaiting(() => Login("admin", "pw", "10.0.0.100")).Should().ThrowAsync<LoginLockedException>("even the right password waits");

        F.Clock.Advance(LoginThrottle.Window);
        (await Login("admin", "pw", "10.0.0.100")).Name.Should().Be("Administrator");
    }

    [Fact]
    public async Task Ten_wrong_passwords_from_one_address_lock_that_address_for_every_name()
    {
        for (var i = 0; i < 10; i++)
            await FluentActions.Awaiting(() => Login($"guess{i}", "wrong", "192.168.1.50")).Should().ThrowAsync<Exception>();
        await FluentActions.Awaiting(() => Login("admin", "pw", "192.168.1.50")).Should().ThrowAsync<LoginLockedException>();
        (await Login("admin", "pw", "192.168.1.51")).Name.Should().Be("Administrator", "another address still signs in");
    }

    [Fact]
    public async Task Old_failures_drop_out_of_the_window()
    {
        for (var i = 0; i < 9; i++)
            await FluentActions.Awaiting(() => Login("admin", "wrong", "10.1.1.1")).Should().ThrowAsync<LoginRejectedException>();
        F.Clock.Advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));
        await FluentActions.Awaiting(() => Login("admin", "wrong", "10.1.1.1")).Should().ThrowAsync<LoginRejectedException>("the first nine are older than 10 minutes");
    }
}
