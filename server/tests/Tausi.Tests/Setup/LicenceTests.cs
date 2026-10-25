using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Application.Settling;
using Tausi.Application.Setup;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.Service;
using Tausi.Domain.Settings;
using Tausi.Licensing;
using Tausi.LicenseTool;
using Tausi.Tests.Tickets;

namespace Tausi.Tests.Setup;

/// <summary>Licence keys (signed, checked offline), the trial, expiry and grace, and what waits for a key.</summary>
public class LicenceTests : ApprovalsFixtureBase
{
    /// <summary>The tests' own key pair; its public key is the server's test key (trusted only in Development).</summary>
    public static readonly byte[] TestPrivateKey = Convert.FromBase64String("ApUa88PWLB1ntYbHOrSTe9NZhgaNvQ+2M1FkHov4YOA=");

    private LicenceService Licences => F.Get<LicenceService>();

    private string Key(string business = "Test", int terminals = 4, DateTimeOffset? expires = null) =>
        LicenceKeys.Issue(new LicencePayload("TL-TEST-1", business, "Standard", terminals, F.Clock.GetUtcNow(), expires, null), TestPrivateKey);

    private async Task AsAdmin() => await F.LoginOn("Administrator", "Counter B");

    [Fact]
    public async Task A_valid_key_is_accepted_and_audited()
    {
        await AsAdmin();
        var status = await Licences.Enter(new EnterLicenceRequest(Key()), asSetup: false, None);
        status.Status.Should().Be("Active");
        status.TerminalLimit.Should().Be(4);
        status.Business.Should().Be("Test");
        F.Read().AuditEntries.Should().ContainSingle(a => a.Action == AuditActions.LicenceEntered);
    }

    [Fact]
    public async Task Tampered_wrong_business_and_test_keys_outside_development_are_refused()
    {
        await AsAdmin();
        var key = Key();
        var tampered = key[..^3] + (key[^3] == 'A' ? 'B' : 'A') + key[^2..];
        (await FluentActions.Awaiting(() => Licences.Enter(new EnterLicenceRequest(tampered), false, None)).Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(LicenceCodes.Invalid);

        (await FluentActions.Awaiting(() => Licences.Enter(new EnterLicenceRequest(Key("Other Bar")), false, None)).Should().ThrowAsync<DomainException>())
            .WithMessage("This key is for 'Other Bar'.");
        (await Licences.Enter(new EnterLicenceRequest(Key("  TEST ")), false, None)).Status.Should().Be("Active", "case and spaces don't matter");

        var production = new LicenceService(F.Db, new LicenceOptions(AcceptTestKey: false), F.Session, F.Get<IPermissionService>(),
            F.Get<IAuditLog>(), F.Events, F.Clock);
        (await FluentActions.Awaiting(() => production.Enter(new EnterLicenceRequest(Key()), false, None)).Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(LicenceCodes.Invalid, "the test key isn't trusted outside Development");
    }

    [Fact]
    public async Task Entering_a_key_needs_settings_manage()
    {
        await F.LoginOn("MAU", "Counter A");
        await FluentActions.Awaiting(() => Licences.Enter(new EnterLicenceRequest(Key()), false, None)).Should().ThrowAsync<PermissionDeniedException>();
    }

    [Fact]
    public async Task The_trial_gives_two_terminals_for_thirty_days_and_counts_down()
    {
        await AsAdmin();
        var trial = await Licences.StartTrial(false, None);
        trial.IsTrial.Should().BeTrue();
        (trial.TerminalLimit, trial.DaysLeft).Should().Be((2, 30));
        F.Clock.Advance(TimeSpan.FromDays(7));
        (await Licences.Status(None)).Banner.Should().Be("Trial: 23 days left · Enter licence key");
        await FluentActions.Awaiting(() => Licences.StartTrial(false, None)).Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Expiry_warns_then_gives_grace_then_only_the_listed_features_wait_for_a_key()
    {
        await AsAdmin();
        await Licences.Enter(new EnterLicenceRequest(Key(expires: F.Clock.GetUtcNow().AddDays(20))), false, None);
        (await Licences.Status(None)).Status.Should().Be("Active");
        F.Clock.Advance(TimeSpan.FromDays(10));
        var soon = await Licences.Status(None);
        (soon.Status, soon.DaysLeft).Should().Be(("ExpiresSoon", 10));
        F.Clock.Advance(TimeSpan.FromDays(15));
        (await Licences.Status(None)).Status.Should().Be("Grace");
        foreach (var feature in Enum.GetValues<LicensedFeature>()) await Licences.Require(feature, None);

        F.Clock.Advance(TimeSpan.FromDays(10));
        (await Licences.Status(None)).Status.Should().Be("Expired");
        foreach (var feature in Enum.GetValues<LicensedFeature>())
            (await FluentActions.Awaiting(() => Licences.Require(feature, None)).Should().ThrowAsync<ForbiddenException>())
                .Which.Code.Should().Be(LicenceCodes.Expired);

        // Selling, printing and settling still work.
        await F.LoginOn("edna", "Counter A");
        var ticket = await Printed("7", ("Pineapple Punch", 1));
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        await StartShift(0);
        await F.Get<PaymentCommands>().Add(ticket.Id, new AddPaymentRequest(PaymentType.Card, (await Detail(ticket.Id)).AmountDue, null, null, "1"), None);
        (await F.Get<SettleTicket>().Execute(ticket.Id, None)).ReceiptNumber.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Turning_the_clock_back_doesnt_extend_a_licence()
    {
        await AsAdmin();
        await Licences.Enter(new EnterLicenceRequest(Key(expires: F.Clock.GetUtcNow().AddDays(5))), false, None);
        // The server has seen a time 40 days on (then the clock was set back to today).
        var licence = F.Db.Licences.Single();
        licence.LastSeenAt = F.Clock.GetUtcNow().AddDays(40);
        await F.Db.SaveChangesAsync();
        (await Licences.Status(None)).Status.Should().Be("Expired");
        LicencePolicy.Now(licence, F.Clock.GetUtcNow()).Should().Be(licence.LastSeenAt);
    }

    [Fact]
    public async Task Over_the_limit_after_a_new_key_shows_but_paired_terminals_keep_working()
    {
        await AsAdmin();
        foreach (var t in F.Db.Terminals.OrderBy(t => t.Name).Take(3)) t.DeviceTokenHash = SecureTokens.Hash(Guid.NewGuid().ToString());
        await F.Db.SaveChangesAsync();
        var paired = F.Read().Terminals.Count(t => t.DeviceTokenHash != null && t.RevokedAt == null);
        paired.Should().Be(3);
        var status = await Licences.Enter(new EnterLicenceRequest(Key(terminals: paired - 1)), false, None);
        status.OverLimit.Should().BeTrue();
        (status.TerminalsUsed, status.TerminalLimit).Should().Be((paired, paired - 1));
        (await F.LoginOn("edna", "Counter A")).TerminalName.Should().Be("Counter A", "a paired terminal over the limit still logs in");
    }

    [Fact]
    public void The_licence_tool_issues_a_key_that_checks_out_and_a_tampered_one_doesnt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tausi-licence-" + Guid.NewGuid().ToString("N"));
        var output = new StringWriter();
        var console = Console.Out;
        Console.SetOut(output);
        try
        {
            LicenceTool.Run(["generate-keypair", folder]).Should().Be(0);
            LicenceTool.Run(["generate-keypair", folder]).Should().Be(1, "an existing private key is never overwritten");
            output.GetStringBuilder().Clear();
            LicenceTool.Run(["issue", Path.Combine(folder, "private.key"), "--business", "Mau's Village", "--terminals", "4", "--expires", "never"])
                .Should().Be(0);
            var key = output.ToString().Trim();
            key.Should().StartWith("TAUSI-");
            var publicKey = File.ReadAllText(Path.Combine(folder, "public.key")).Trim();
            LicenceKeys.TryRead(key, [Convert.FromBase64String(publicKey)], out var payload).Should().Be(LicenceKeyProblem.None);
            (payload!.Business, payload.MaxTerminals, payload.Expires).Should().Be(("Mau's Village", 4, (DateTimeOffset?)null));
            LicenceTool.Run(["verify", Path.Combine(folder, "public.key"), key]).Should().Be(0);
            LicenceTool.Run(["verify", Path.Combine(folder, "public.key"), key.Replace("TAUSI-A", "TAUSI-B")]).Should().NotBe(0);
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(folder, true);
        }
    }
}
