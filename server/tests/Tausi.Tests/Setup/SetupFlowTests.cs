using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Setup;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Setup;
using Tausi.Licensing;
using Tausi.Tests.Tickets;

namespace Tausi.Tests.Setup;

/// <summary>The first-run wizard on a new install: token, steps 1 and 2, progress and Finish.</summary>
public class SetupFlowTests : IAsyncLifetime
{
    private TicketsFixture F = null!;
    private static readonly CancellationToken None = CancellationToken.None;

    public async Task InitializeAsync() => F = await TicketsFixture.CreateEmpty();
    public async Task DisposeAsync() => await F.DisposeAsync();

    private SetupFlow Flow => F.Get<SetupFlow>();

    private static SetupBusinessRequest Mau(string type = BusinessTypes.Bar) =>
        new("Mau's Village", type, "Ruiru Road", "0700 000 000", 16, "KES", "Africa/Nairobi");

    private static SetupAdminRequest Owner(string pin = "4826", string? again = null, string password = "a long password") =>
        new("Wanjiku Mau", "wanjiku", password, pin, again ?? pin);

    private async Task<SessionUser> Through(int step)
    {
        await F.Get<SetupBusiness>().Save(Mau(), None);
        if (step < 2) return null!;
        var owner = await F.Get<SetupPeople>().Admin(Owner(), None);
        F.Session.User = owner;
        return owner;
    }

    [Fact]
    public async Task A_new_install_is_in_setup_mode_and_seeded_ones_are_not()
    {
        (await SetupFlow.InProgress(F.Db, None)).Should().BeTrue();
        (await F.Get<SetupGate>().IsComplete(F.Db, None)).Should().BeFalse();
        await using var demo = await TicketsFixture.Create();
        (await SetupFlow.InProgress(demo.Db, None)).Should().BeFalse("seeding (dev, demo, CI) marks setup complete");
    }

    [Fact]
    public async Task The_token_unlocks_the_wizard_and_five_wrong_tries_lock_it_for_five_minutes()
    {
        var token = await Flow.EnsureToken(None);
        token.Should().MatchRegex("^[A-Z2-9]{4}-[A-Z2-9]{4}$");
        F.TokenFile.Token.Should().Be(token);
        (await Flow.EnsureToken(None)).Should().Be(token, "a restart keeps the token in setup-token.txt");

        for (var i = 1; i <= 4; i++)
            (await FluentActions.Awaiting(() => Flow.Unlock("WRON-GTOK", None)).Should().ThrowAsync<DomainException>())
                .Which.Message.Should().Contain($"{5 - i} tries left");
        await FluentActions.Awaiting(() => Flow.Unlock("WRON-GTOK", None)).Should().ThrowAsync<LoginLockedException>();
        await FluentActions.Awaiting(() => Flow.Unlock(token, None)).Should().ThrowAsync<LoginLockedException>("locked even with the right token");

        F.Clock.Advance(TimeSpan.FromMinutes(5));
        var session = await Flow.Unlock(token!.ToLowerInvariant().Replace("-", " "), None);
        await Flow.RequireSession(session, None);
        await FluentActions.Awaiting(() => Flow.RequireSession("someone-else", None)).Should().ThrowAsync<UnauthenticatedException>();
        (await Flow.Status(session, None)).Unlocked.Should().BeTrue();
        (await Flow.Status("someone-else", None)).Steps.Should().OnlyContain(s => s.Status == "Todo", "nothing shows without the session");
    }

    [Fact]
    public async Task Step_one_saves_the_business_as_audited_setting_changes_by_setup_with_the_type_defaults()
    {
        await F.Get<SetupBusiness>().Save(Mau(), None);

        var settings = await SettingsRegistry.Load(F.Read(), None);
        settings.String(SettingsRegistry.BusinessName).Should().Be("Mau's Village");
        settings.String(SettingsRegistry.BusinessTimeZoneKey).Should().Be("Africa/Nairobi");
        settings.String(SettingsRegistry.PrintModeKey).Should().Be("OneButton");
        var audits = F.Read().AuditEntries.Where(a => a.Action == AuditActions.SettingsChanged).ToList();
        audits.Should().NotBeEmpty().And.OnlyContain(a => a.Actor == SetupFlow.SetupActor && a.UserId == null);
        var temperature = F.Read().ModifierGroups.Include(g => g.Options).Single(g => g.Name == "Temperature");
        temperature.Options.Select(o => o.Name).Should().Equal("Cold", "Warm");
        temperature.Options.Single(o => o.IsDefault).Name.Should().Be("Cold");
        F.Read().SetupProgress.Single().Step.Should().Be(SetupSteps.Business);

        await FluentActions.Awaiting(() => F.Get<SetupBusiness>().Save(Mau() with { Name = " " }, None)).Should().ThrowAsync<FieldErrorsException>();
        await FluentActions.Awaiting(() => F.Get<SetupBusiness>().Save(Mau() with { TimeZone = "Mars/Base" }, None)).Should().ThrowAsync<FieldErrorsException>();
    }

    [Fact]
    public async Task A_restaurant_gets_no_temperature_options()
    {
        await F.Get<SetupBusiness>().Save(Mau(BusinessTypes.Restaurant), None);
        F.Read().ModifierGroups.Should().BeEmpty();
        (await Flow.State(None)).BusinessType.Should().Be(BusinessTypes.Restaurant);
    }

    [Fact]
    public async Task Step_two_makes_the_owner_with_the_password_and_pin_rules_and_no_bootstrap_admin()
    {
        await F.Get<SetupBusiness>().Save(Mau(), None);
        var people = F.Get<SetupPeople>();
        (await FluentActions.Awaiting(() => people.Admin(Owner(password: "short"), None)).Should().ThrowAsync<FieldErrorsException>())
            .Which.Errors.Keys.Should().Contain("password");
        (await FluentActions.Awaiting(() => people.Admin(Owner(again: "4827"), None)).Should().ThrowAsync<FieldErrorsException>())
            .Which.Errors.Keys.Should().Contain("pinAgain");
        (await FluentActions.Awaiting(() => people.Admin(Owner(pin: "1234"), None)).Should().ThrowAsync<FieldErrorsException>())
            .Which.Errors["pin"].Should().Contain("too easy");
        (await FluentActions.Awaiting(() => people.Admin(Owner(pin: "48261"), None)).Should().ThrowAsync<FieldErrorsException>())
            .Which.Errors["pin"].Should().Be("Enter 4 digits.");

        var owner = await people.Admin(Owner(), None);
        owner.RoleName.Should().Be("Admin");
        var users = F.Read().Users.ToList();
        users.Should().ContainSingle("the TAUSI_ADMIN_PASSWORD bootstrap admin is never made on a wizard install");
        users[0].UserName.Should().Be("wanjiku");
        users[0].MustChangePin.Should().BeFalse();
        F.Hasher.Verify("4826", users[0].PinHash).Should().BeTrue();
        F.Hasher.Verify("a long password", users[0].PasswordHash!).Should().BeTrue();

        // Back and Next again updates the same owner.
        await people.Admin(Owner(pin: "5937") with { FullName = "Wanjiku M." }, None);
        F.Read().Users.Should().ContainSingle().Which.Name.Should().Be("Wanjiku M.");
    }

    [Fact]
    public async Task Staff_get_random_pins_they_must_change_shown_once_with_the_pin_sheet()
    {
        await Through(2);
        var result = await F.Get<SetupPeople>().Staff(new SetupStaffRequest(
        [
            new StaffToAdd("edna", "Waiter"), new StaffToAdd("CAROL", "cashier"), new StaffToAdd("MAU", "Supervisor"), new StaffToAdd("Ruth", "waiter"),
        ]), None);

        result.People.Should().HaveCount(4);
        result.People.Select(p => p.Pin).Should().OnlyHaveUniqueItems().And.OnlyContain(p => p.Length == 4 && !Pin.IsTooSimple(p));
        var saved = F.Read().Users.Include(u => u.Role).Where(u => u.Name != "Wanjiku Mau").ToList();
        saved.Should().HaveCount(4).And.OnlyContain(u => u.MustChangePin && u.PasswordHash == null);
        foreach (var person in result.People) F.Hasher.Verify(person.Pin, saved.Single(u => u.Id == person.Id).PinHash).Should().BeTrue();
        saved.Single(u => u.Name == "CAROL").Role!.Name.Should().Be("Cashier");
        System.Text.Encoding.Latin1.GetString(Convert.FromBase64String(result.PinSheetPdf)).Should().StartWith("%PDF").And.Contain("Change this PIN at first login.");
        F.Read().AuditEntries.Where(a => a.Action == AuditActions.StaffCreated).ToList().Should().HaveCount(5)
            .And.OnlyContain(a => a.NewValueJson!.Contains("\"name\"") && !a.NewValueJson.Contains("pin\":\""), "PINs are never in the audit log");

        await FluentActions.Awaiting(() => F.Get<SetupPeople>().Staff(new SetupStaffRequest([new StaffToAdd("Kim", "Admin")]), None))
            .Should().ThrowAsync<FieldErrorsException>();
    }

    [Fact]
    public async Task Progress_is_saved_so_the_wizard_resumes_and_only_steps_3_to_5_can_be_skipped()
    {
        var session = await Flow.Unlock(await Flow.EnsureToken(None), None);
        (await Flow.Status(session, None)).Current.Should().Be(SetupSteps.Business);
        await Through(2);
        await Flow.Mark(SetupSteps.Devices, SetupStepStatus.Skipped, null, None);
        var status = await Flow.Status(session, None);
        status.Current.Should().Be(SetupSteps.Menu);
        status.Steps.Select(s => s.Status).Should().Equal("Done", "Done", "Skipped", "Todo", "Todo", "Todo");
        status.BusinessName.Should().Be("Mau's Village");
        await FluentActions.Awaiting(() => Flow.Mark(SetupSteps.Licence, SetupStepStatus.Skipped, null, None)).Should().ThrowAsync<DomainException>();
        await FluentActions.Awaiting(() => Flow.Mark(SetupSteps.Admin, SetupStepStatus.Skipped, null, None)).Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Finish_needs_the_required_steps_then_ends_setup_and_the_token()
    {
        var token = await Flow.EnsureToken(None);
        var session = await Flow.Unlock(token, None);
        await Through(2);
        (await FluentActions.Awaiting(() => Flow.Finish(None)).Should().ThrowAsync<DomainException>()).Which.Message.Should().Contain("Licence key");

        await F.Get<LicenceService>().StartTrial(asSetup: true, None);
        await Flow.Mark(SetupSteps.Licence, SetupStepStatus.Done, "trial", None);
        var summary = await F.Get<SetupSummary>().Build(None);
        summary.Lines.Single(l => l.Step == SetupSteps.Menu).Should().Match<SummaryLine>(l => l.Skipped && l.LaterLink == "/admin/menu");
        summary.Lines.Single(l => l.Step == SetupSteps.Licence).Text.Should().StartWith("30-day trial");

        await Flow.Finish(None);
        (await SetupFlow.InProgress(F.Db, None)).Should().BeFalse();
        (await F.Get<SetupGate>().IsComplete(F.Db, None)).Should().BeTrue();
        F.TokenFile.Token.Should().BeNull("setup-token.txt is deleted");
        F.Events.Published.Should().Contain(e => e.Name == PosEventNames.SetupCompleted);
        F.Read().AuditEntries.Should().Contain(a => a.Action == AuditActions.SetupCompleted && a.Actor == SetupFlow.SetupActor);
        (await FluentActions.Awaiting(() => Flow.Unlock(token, None)).Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(SetupCodes.Complete);
        await FluentActions.Awaiting(() => Flow.RequireSession(session, None)).Should().ThrowAsync<ConflictException>();
        (await Flow.Status(null, None)).Complete.Should().BeTrue();
    }

    [Fact]
    public async Task A_licence_key_is_checked_against_the_business_from_step_one()
    {
        await Through(1);
        var key = LicenceKeys.Issue(new LicencePayload("TL-1", "MAU'S VILLAGE", "Standard", 4, F.Clock.GetUtcNow(), null, null), LicenceTests.TestPrivateKey);
        (await F.Get<LicenceService>().Enter(new EnterLicenceRequest(key), asSetup: true, None)).TerminalLimit.Should().Be(4);
        F.Read().AuditEntries.Should().Contain(a => a.Action == AuditActions.LicenceEntered && a.Actor == SetupFlow.SetupActor);
    }
}
