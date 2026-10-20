using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Staff;
using Tausi.Domain.Common;
using Tausi.Domain.People;

namespace Tausi.Tests.Staff;

public class StaffTests : IAsyncLifetime
{
    private StaffFixture _f = null!;

    public async Task InitializeAsync() => _f = await StaffFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Guid Role(string name) => _f.Roles[name].Id;
    private Guid Id(string name) => _f.Read().Users.Single(u => u.Name == name).Id;

    private Task<StaffSaved> Create(string name, string role = "Waiter", string pin = "6284", string? password = null) =>
        _f.Get<CreateStaff>().Execute(new CreateStaffRequest(name, Role(role), pin, password), CancellationToken.None);

    private Task<StaffMemberDto> Update(string name, string newName, string role, string? password = null) =>
        _f.Get<UpdateStaff>().Execute(Id(name), new UpdateStaffRequest(newName, Role(role), password, _f.Version(name)), CancellationToken.None);

    private Task<StaffMemberDto> Deactivate(string name) =>
        _f.Get<DeactivateStaff>().Execute(Id(name), new RowVersionRequest(_f.Version(name)), CancellationToken.None);

    private Task<StaffSaved> Reset(string name, string? pin = null) =>
        _f.Get<ResetStaffPin>().Execute(Id(name), new SetPinRequest(pin, pin is null, _f.Version(name)), CancellationToken.None);

    private static async Task<string?> CodeOf(Func<Task> act)
    {
        try
        {
            await act();
            return null;
        }
        catch (DomainException e) { return e.Code; }
        catch (ForbiddenException e) { return e.Code; }
        catch (ConflictException e) { return e.Code; }
        catch (NotFoundException) { return "not_found"; }
    }

    // ---- Create ----

    [Fact]
    public async Task New_people_must_change_their_pin_and_the_pin_is_returned_once()
    {
        var saved = await Create("Grace");

        saved.TemporaryPin.Should().Be("6284");
        saved.Member.MustChangePin.Should().BeTrue();
        var grace = await _f.Read().Users.SingleAsync(u => u.Name == "Grace");
        grace.MustChangePin.Should().BeTrue();
        grace.PinHash.Should().NotContain("6284");
        (await _f.Get<GetStaffMember>().Execute(grace.Id, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case()
    {
        await Create("Grace");
        (await CodeOf(() => Create(" GRACE ", pin: "9157"))).Should().Be(StaffCodes.NameTaken);
        (await CodeOf(() => Create("EDNA", pin: "9157"))).Should().Be(StaffCodes.NameTaken);
    }

    [Theory]
    [InlineData("G")]
    [InlineData("A name that is far too long for a staff list x")]
    [InlineData("   ")]
    public async Task Names_must_be_2_to_40_characters(string name) =>
        (await CodeOf(() => Create(name))).Should().Be(StaffCodes.NameInvalid);

    [Theory]
    [InlineData("5739", PinChangeCodes.Taken)]
    [InlineData("1234", PinChangeCodes.TooSimple)]
    [InlineData("0000", PinChangeCodes.TooSimple)]
    [InlineData("12a4", PinChangeCodes.Invalid)]
    public async Task Starting_pins_follow_the_pin_rules(string pin, string code) =>
        (await CodeOf(() => Create("Grace", pin: pin))).Should().Be(code);

    [Fact]
    public async Task Supervisors_and_admins_need_an_admin_site_password_of_10_characters()
    {
        (await CodeOf(() => Create("Wanjiru", "Supervisor"))).Should().Be(StaffCodes.PasswordRequired);
        (await CodeOf(() => Create("Wanjiru", "Supervisor", password: "short"))).Should().Be(StaffCodes.PasswordTooShort);

        var saved = await Create("Wanjiru", "Supervisor", password: "a-long-password");
        saved.Member.HasAdminPassword.Should().BeTrue();
        saved.Member.AdminUserName.Should().Be("wanjiru");
    }

    // ---- Supervisor limits ----

    [Fact]
    public async Task A_supervisor_sees_and_offers_only_waiters_and_cashiers()
    {
        _f.ActAs("MAU");
        var list = await _f.Get<ListStaff>().Execute(CancellationToken.None);

        list.Members.Select(m => m.Name).Should().BeEquivalentTo(["CAROL", "edna", "Ruth"]);
        list.AssignableRoles.Select(r => r.Name).Should().BeEquivalentTo(["Cashier", "Waiter"]);
    }

    [Fact]
    public async Task A_supervisor_can_add_and_edit_waiters()
    {
        _f.ActAs("MAU");
        await Create("Grace");
        await Update("edna", "Edna", "Cashier");
        (await _f.Read().Users.SingleAsync(u => u.Name == "Edna")).RoleId.Should().Be(Role("Cashier"));
    }

    [Fact]
    public async Task A_supervisor_cannot_promote_anyone()
    {
        _f.ActAs("MAU");
        (await CodeOf(() => Update("edna", "edna", "Supervisor"))).Should().Be(StaffCodes.RoleNotAssignable);
        (await CodeOf(() => Create("Grace", "Admin", password: "a-long-password"))).Should().Be(StaffCodes.RoleNotAssignable);
    }

    [Fact]
    public async Task A_supervisor_cannot_touch_admins_or_supervisors_including_themselves()
    {
        _f.ActAs("MAU");
        (await CodeOf(() => Update("Administrator", "Admin2", "Admin"))).Should().Be("not_found");
        (await CodeOf(() => Deactivate("Administrator"))).Should().Be("not_found");
        (await CodeOf(() => Reset("Administrator"))).Should().Be("not_found");
        (await CodeOf(() => Update("MAU", "Mau", "Supervisor"))).Should().Be(StaffCodes.NotManageable);
        (await CodeOf(() => Deactivate("MAU"))).Should().Be(StaffCodes.SelfDeactivate);
    }

    [Fact]
    public async Task Without_staff_manage_nothing_works()
    {
        _f.ActAs("CAROL");
        await FluentActions.Awaiting(() => _f.Get<ListStaff>().Execute(CancellationToken.None))
            .Should().ThrowAsync<Tausi.Application.Security.PermissionDeniedException>();
        await FluentActions.Awaiting(() => Create("Grace"))
            .Should().ThrowAsync<Tausi.Application.Security.PermissionDeniedException>();
    }

    // ---- Admin limits ----

    [Fact]
    public async Task An_admin_manages_everyone_but_not_their_own_role()
    {
        await Update("MAU", "MAU", "Waiter");
        (await CodeOf(() => Update("Administrator", "Administrator", "Waiter"))).Should().Be(StaffCodes.OwnRole);
        await Update("Administrator", "Admin Person", "Admin");
    }

    [Fact]
    public async Task The_last_admin_cannot_be_deactivated()
    {
        var ex = await FluentActions.Awaiting(() => Deactivate("Administrator")).Should().ThrowAsync<DomainException>();
        ex.Which.Code.Should().Be(StaffCodes.LastAdmin);
        ex.Which.Message.Should().Be("There must be at least one active Admin.");
    }

    [Fact]
    public async Task An_admin_cannot_deactivate_themselves_even_with_another_admin()
    {
        await Create("Second Admin", "Admin", pin: "9157", password: "a-long-password");

        (await CodeOf(() => Deactivate("Administrator"))).Should().Be(StaffCodes.SelfDeactivate);
    }

    [Fact]
    public async Task Added_roles_count_as_waiter_level_unless_they_have_staff_manage()
    {
        var host = new Role { Name = "Host" };
        var floor = new Role { Name = "Floor manager" };
        floor.Permissions.Add(new RolePermission { PermissionKey = PermissionKeys.StaffManage, Value = PermissionValue.Allowed });
        _f.Db.Roles.AddRange(host, floor);
        await _f.Db.SaveChangesAsync();

        _f.ActAs("MAU");
        var roles = (await _f.Get<ListStaff>().Execute(CancellationToken.None)).AssignableRoles.Select(r => r.Name);
        roles.Should().Contain("Host").And.NotContain("Floor manager");
    }

    // ---- Reset, deactivate, reactivate ----

    [Fact]
    public async Task Reset_pin_forces_a_change_ends_sessions_and_announces_it()
    {
        var keeper = _f.Get<SessionKeeper>();
        var session = await keeper.Start(Id("edna"), _f.Terminal.Id, CancellationToken.None);
        await _f.Db.SaveChangesAsync();

        var saved = await Reset("edna");

        saved.TemporaryPin.Should().MatchRegex("^[0-9]{4,6}$");
        Pin.IsTooSimple(saved.TemporaryPin!).Should().BeFalse();
        var edna = await _f.Read().Users.SingleAsync(u => u.Id == Id("edna"));
        edna.MustChangePin.Should().BeTrue();
        _f.Hasher.Verify(saved.TemporaryPin!, edna.PinHash).Should().BeTrue();
        (await _f.Get<SessionKeeper>().Check(session.Id, Id("edna"), _f.Terminal.Id, true, CancellationToken.None))
            .Should().Be(SessionEndReasons.PinReset);
        _f.Events.Published.Should().ContainSingle(e => e.Name == PosEventNames.UserPinReset);
    }

    [Fact]
    public async Task Reset_pin_follows_the_pin_rules()
    {
        (await CodeOf(() => Reset("edna", "5739"))).Should().Be(PinChangeCodes.Taken);
        (await CodeOf(() => Reset("edna", "9876"))).Should().Be(PinChangeCodes.TooSimple);
        (await Reset("edna", "3958")).TemporaryPin.Should().Be("3958");
    }

    [Fact]
    public async Task Deactivate_ends_sessions_blocks_login_and_keeps_the_name_taken()
    {
        var keeper = _f.Get<SessionKeeper>();
        var session = await keeper.Start(Id("Ruth"), _f.Terminal.Id, CancellationToken.None);
        await _f.Db.SaveChangesAsync();

        var member = await Deactivate("Ruth");

        member.Active.Should().BeFalse();
        (await _f.Get<SessionKeeper>().Check(session.Id, Id("Ruth"), _f.Terminal.Id, true, CancellationToken.None))
            .Should().Be(SessionEndReasons.Deactivated);
        _f.Events.Published.Should().ContainSingle(e => e.Name == PosEventNames.UserDeactivated);
        await FluentActions.Awaiting(() => _f.Get<PinAuthenticator>().Authenticate(_f.Terminal.Id, "2365", CancellationToken.None))
            .Should().ThrowAsync<LoginRejectedException>();
        (await CodeOf(() => Create("ruth", pin: "9157"))).Should().Be(StaffCodes.NameTaken);

        var list = await _f.Get<ListStaff>().Execute(CancellationToken.None);
        list.Members.Last().Name.Should().Be("Ruth", "inactive people are listed after active ones");
    }

    [Fact]
    public async Task Reactivate_needs_a_new_pin_and_forces_a_change()
    {
        await Deactivate("Ruth");
        (await CodeOf(() => _f.Get<ReactivateStaff>().Execute(Id("Ruth"), new SetPinRequest("", false, _f.Version("Ruth")), CancellationToken.None)))
            .Should().Be(PinChangeCodes.Invalid);

        var saved = await _f.Get<ReactivateStaff>().Execute(Id("Ruth"), new SetPinRequest(null, true, _f.Version("Ruth")), CancellationToken.None);

        saved.Member.Active.Should().BeTrue();
        saved.Member.MustChangePin.Should().BeTrue();
        saved.TemporaryPin.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Only_create_reset_and_reactivate_answers_carry_a_pin()
    {
        (await Create("Grace")).TemporaryPin.Should().NotBeNull();
        var list = await _f.Get<ListStaff>().Execute(CancellationToken.None);
        System.Text.Json.JsonSerializer.Serialize(list).Should().NotContain("6284");
    }

    [Fact]
    public async Task Audit_entries_never_contain_pins_or_passwords()
    {
        await Create("Grace", pin: "6284");
        await Create("Wanjiru", "Supervisor", pin: "9157", password: "secret-password-1");
        await Update("Wanjiru", "Wanjiru", "Supervisor", password: "secret-password-2");
        await Reset("edna", "3958");
        await Deactivate("Grace");
        await _f.Get<ReactivateStaff>().Execute(Id("Grace"), new SetPinRequest("7146", false, _f.Version("Grace")), CancellationToken.None);

        var audit = await _f.Read().AuditEntries.Where(a => a.Action.StartsWith("staff.")).ToListAsync();
        audit.Select(a => a.Action).Should().Contain([
            AuditActions.StaffCreated, AuditActions.StaffUpdated, AuditActions.StaffPinReset,
            AuditActions.StaffDeactivated, AuditActions.StaffReactivated]);
        var text = string.Join("\n", audit.Select(a => a.OldValueJson + a.NewValueJson));
        foreach (var secret in new[] { "6284", "9157", "3958", "7146", "secret-password", "fast$", "pbkdf2" })
            text.Should().NotContain(secret);
        text.Should().NotContainEquivalentOf("pinHash").And.NotContainEquivalentOf("passwordHash");
    }


    [Fact]
    public async Task An_edit_based_on_an_old_version_is_refused()
    {
        var old = _f.Version("edna");
        await Update("edna", "Edna", "Waiter");

        var ex = await FluentActions.Awaiting(() => _f.Get<UpdateStaff>().Execute(
            Id("Edna"), new UpdateStaffRequest("Edna W", Role("Waiter"), null, old), CancellationToken.None))
            .Should().ThrowAsync<ConflictException>();
        ex.Which.Message.Should().Be("This person was changed by someone else. Reload and try again.");
    }

    [Fact]
    public async Task Terminals_used_come_from_the_last_7_days_of_logins()
    {
        var now = _f.Clock.GetUtcNow();
        var pillar = new Domain.Devices.Terminal { Name = "Pillar 1" };
        var old = new Domain.Devices.Terminal { Name = "Counter B" };
        _f.Db.Terminals.AddRange(pillar, old);
        void Login(Guid terminal, TimeSpan ago, string action = AuditActions.PinLogin) =>
            _f.Db.AuditEntries.Add(new Domain.Audit.AuditEntry { Action = action, UserId = Id("edna"), TerminalId = terminal, At = now - ago });
        Login(_f.Terminal.Id, TimeSpan.FromDays(1));
        Login(_f.Terminal.Id, TimeSpan.FromDays(2));
        Login(pillar.Id, TimeSpan.FromHours(3));
        Login(old.Id, TimeSpan.FromDays(8));
        Login(old.Id, TimeSpan.FromHours(1), AuditActions.PinLoginFailed);
        await _f.Db.SaveChangesAsync();

        var edna = await _f.Get<GetStaffMember>().Execute(Id("edna"), CancellationToken.None);

        edna.TerminalsLast7Days.Should().Equal("Counter A", "Pillar 1");
        edna.LastLogin!.TerminalName.Should().Be("Pillar 1");
        edna.LastLogin.At.Should().Be(now - TimeSpan.FromHours(3));
    }
}

public class StaffRulesTests
{
    [Theory]
    [InlineData(StaffTier.Admin, StaffTier.Admin, true)]
    [InlineData(StaffTier.Admin, StaffTier.Manager, true)]
    [InlineData(StaffTier.Admin, StaffTier.Staff, true)]
    [InlineData(StaffTier.Manager, StaffTier.Admin, false)]
    [InlineData(StaffTier.Manager, StaffTier.Manager, false)]
    [InlineData(StaffTier.Manager, StaffTier.Staff, true)]
    [InlineData(StaffTier.Staff, StaffTier.Staff, false)]
    public void Who_manages_whom(StaffTier actor, StaffTier target, bool expected) =>
        StaffRules.CanManage(actor, target).Should().Be(expected);

    [Theory]
    [InlineData(RoleKeys.Admin, false, StaffTier.Admin)]
    [InlineData(RoleKeys.Supervisor, false, StaffTier.Manager)]
    [InlineData(RoleKeys.Waiter, true, StaffTier.Staff)]
    [InlineData(null, false, StaffTier.Staff)]
    [InlineData(null, true, StaffTier.Manager)]
    public void Tiers_come_from_the_built_in_key_or_staff_manage(string? key, bool staffManage, StaffTier tier) =>
        StaffRules.TierOf(key, staffManage).Should().Be(tier);
}
