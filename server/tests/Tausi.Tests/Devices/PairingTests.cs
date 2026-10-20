using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Terminals;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Settings;

namespace Tausi.Tests.Devices;

public class PairingTests : IAsyncLifetime
{
    private DevicesFixture _f = null!;

    public async Task InitializeAsync() => _f = await DevicesFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private async Task<(string Code, PairingStarted Started)> NewCode()
    {
        var started = await _f.Get<StartPairing>().Execute(CancellationToken.None);
        return (started.Code, started);
    }

    private Task<PairingCompleted> Pair(string code, Guid? terminalId = null, string? newName = null, bool confirm = false) =>
        _f.Get<PairTerminal>().Execute(new PairTerminalRequest(code, terminalId, newName, confirm), CancellationToken.None);

    private Task<PairingClaim> Claim(PairingStarted s) =>
        _f.Get<ClaimPairing>().Execute(new ClaimPairingRequest(s.PairingId, s.Secret), CancellationToken.None);

    private static async Task<string?> CodeOf(Func<Task> act)
    {
        try { await act(); return null; }
        catch (DomainException e) { return e.Code; }
        catch (ConflictException e) { return e.Code; }
        catch (LoginLockedException) { return "blocked"; }
    }

    [Fact]
    public async Task Pairing_a_new_terminal_gives_the_app_its_token_once()
    {
        var (code, started) = await NewCode();
        var paired = await Pair(code, newName: "Bar Counter");

        var claim = await Claim(started);
        claim.Status.Should().Be("paired");
        claim.TerminalName.Should().Be("Bar Counter");
        _f.Terminal("Bar Counter").DeviceTokenHash.Should().Be(SecureTokens.Hash(claim.DeviceToken!));
        paired.TerminalName.Should().Be("Bar Counter");
        (await _f.Read().AuditEntries.CountAsync(a => a.Action == "terminal.paired")).Should().Be(1);

        await FluentActions.Awaiting(() => Claim(started)).Should().ThrowAsync<NotFoundException>("the token is handed out once");
    }

    [Fact]
    public async Task A_wrong_or_expired_or_used_code_is_refused()
    {
        (await CodeOf(() => Pair("000000", newName: "X1"))).Should().Be(PairingCodes.CodeNotFound);

        var (old, _) = await NewCode();
        _f.Clock.Advance(TimeSpan.FromMinutes(11));
        (await CodeOf(() => Pair(old, newName: "X2"))).Should().Be(PairingCodes.CodeNotFound);

        var (used, _) = await NewCode();
        await Pair(used, newName: "X3");
        (await CodeOf(() => Pair(used, newName: "X4"))).Should().Be(PairingCodes.CodeNotFound);
    }

    [Fact]
    public async Task Five_wrong_codes_in_five_minutes_block_for_five_minutes()
    {
        for (var i = 0; i < 4; i++)
            (await CodeOf(() => Pair("111111", newName: "X"))).Should().Be(PairingCodes.CodeNotFound);
        (await CodeOf(() => Pair("111111", newName: "X"))).Should().Be("blocked");

        var (good, _) = await NewCode();
        (await CodeOf(() => Pair(good, newName: "Blocked anyway"))).Should().Be("blocked");

        _f.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var (fresh, _) = await NewCode();
        await Pair(fresh, newName: "Allowed again");
    }

    [Fact]
    public async Task Re_pairing_needs_confirmation_and_revokes_the_old_device()
    {
        var (first, s1) = await NewCode();
        await Pair(first, newName: "Pillar 3");
        var oldToken = (await Claim(s1)).DeviceToken!;
        var terminal = _f.Terminal("Pillar 3");
        var session = await _f.Get<SessionKeeper>().Start(_f.Admin.Id, terminal.Id, CancellationToken.None);
        await _f.Db.SaveChangesAsync();

        var (second, s2) = await NewCode();
        (await CodeOf(() => Pair(second, terminal.Id))).Should().Be(PairingCodes.AlreadyPaired);

        await Pair(second, terminal.Id, confirm: true);
        var newToken = (await Claim(s2)).DeviceToken!;

        _f.Terminal("Pillar 3").DeviceTokenHash.Should().Be(SecureTokens.Hash(newToken)).And.NotBe(SecureTokens.Hash(oldToken));
        (await _f.Get<SessionKeeper>().Check(session.Id, _f.Admin.Id, terminal.Id, true, CancellationToken.None)).Should().NotBeNull();
        _f.Events.Published.Should().Contain(e => e.Name == PosEventNames.TerminalRevoked);
    }

    [Fact]
    public async Task Revoking_ends_access_and_tells_the_app()
    {
        var counterA = _f.Terminal("Counter A");
        _f.Db.Terminals.Single(t => t.Id == counterA.Id).DeviceTokenHash = SecureTokens.Hash("token-a");
        await _f.Db.SaveChangesAsync();
        var session = await _f.Get<SessionKeeper>().Start(_f.Admin.Id, counterA.Id, CancellationToken.None);
        await _f.Db.SaveChangesAsync();

        await _f.Get<RevokeTerminal>().Execute(counterA.Id, CancellationToken.None);

        var revoked = _f.Terminal("Counter A");
        revoked.DeviceTokenHash.Should().BeNull();
        revoked.RevokedAt.Should().NotBeNull();
        (await _f.Get<SessionKeeper>().Check(session.Id, _f.Admin.Id, counterA.Id, true, CancellationToken.None))
            .Should().Be(SessionEndReasons.Revoked);
        _f.Events.Published.Should().Contain(e => e.Name == PosEventNames.TerminalRevoked);
        (await _f.Get<ListTerminals>().Execute(CancellationToken.None)).Single(t => t.Name == "Counter A").Status
            .Should().Be(TerminalStatus.Revoked);
    }

    [Fact]
    public async Task Without_a_licence_there_is_no_limit_and_with_one_the_limit_holds()
    {
        for (var i = 0; i < 5; i++)
        {
            var (code, _) = await NewCode();
            await Pair(code, newName: $"Free {i}");
        }

        _f.Db.Licences.Add(new Licence { Key = "K", BusinessName = "Test", TerminalLimit = 5 });
        await _f.Db.SaveChangesAsync();
        var (over, _) = await NewCode();
        var ex = await FluentActions.Awaiting(() => Pair(over, newName: "One too many")).Should().ThrowAsync<DomainException>();
        ex.Which.Code.Should().Be(PairingCodes.LicenceLimit);
        ex.Which.Message.Should().Be("Your licence allows 5 terminals.");

        // Re-pairing a terminal already counted is fine, and a revoked one frees a place.
        var (again, _) = await NewCode();
        await Pair(again, _f.Terminal("Free 0").Id, confirm: true);
        await _f.Get<RevokeTerminal>().Execute(_f.Terminal("Free 1").Id, CancellationToken.None);
        var (place, _) = await NewCode();
        await Pair(place, newName: "Fits now");
    }
}

public class TerminalPresenceTests
{
    [Fact]
    public void Online_while_connected_and_offline_30_seconds_after_the_last_connection_closes()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var presence = new TerminalPresence(clock);
        var terminal = Guid.NewGuid();

        presence.IsOnline(terminal).Should().BeFalse();
        presence.Connected(terminal, "c1");
        presence.Connected(terminal, "c2");
        presence.TakeChanges().Should().Equal((terminal, true));
        presence.TakeChanges().Should().BeEmpty();

        presence.Disconnected("c1");
        clock.Advance(TimeSpan.FromMinutes(5));
        presence.IsOnline(terminal).Should().BeTrue("another connection is still open");

        presence.Disconnected("c2");
        clock.Advance(TimeSpan.FromSeconds(29));
        presence.IsOnline(terminal).Should().BeTrue();
        presence.TakeChanges().Should().BeEmpty();

        clock.Advance(TimeSpan.FromSeconds(2));
        presence.IsOnline(terminal).Should().BeFalse();
        presence.TakeChanges().Should().Equal((terminal, false));
        presence.LastDisconnect(terminal).Should().NotBeNull();

        presence.Connected(terminal, "c3");
        presence.TakeChanges().Should().Equal((terminal, true));
    }
}
