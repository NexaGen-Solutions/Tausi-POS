using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Roles;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Tests.Tickets;

namespace Tausi.Tests.Security;

public class PermissionsAdminTests : IAsyncLifetime
{
    private TicketsFixture F = null!;
    private static readonly CancellationToken None = CancellationToken.None;

    public async Task InitializeAsync()
    {
        F = await TicketsFixture.Create();
        await F.LoginOn("Administrator", "Counter A");
    }

    public async Task DisposeAsync() => await F.DisposeAsync();

    private Guid Role(string name) => F.Read().Roles.Single(r => r.Name == name).Id;

    private async Task<PermissionsGridDto> Save(params (string Role, string Key, PermissionValue Value)[] cells)
    {
        var grid = await F.Get<GetPermissions>().Execute(None);
        return await F.Get<SavePermissions>().Execute(new SavePermissionsRequest(grid.Version,
            cells.Select(c => new PermissionCellChange(Role(c.Role), c.Key, c.Value)).ToList()), None);
    }

    private (string, string, PermissionValue)[] EveryRole(string key, PermissionValue value) =>
        F.Read().Roles.AsEnumerable().Select(r => (r.Name, key, value)).ToArray();

    [Fact]
    public void Keys_that_can_not_be_approved_are_settle_menu_reports_day_and_admin()
    {
        PermissionCatalog.All.Select(p => p.Key).Should().Equal(PermissionKeys.All, "every key, in the order of AGENTS.md");
        PermissionCatalog.NotApprovable.Should().BeEquivalentTo(
            PermissionKeys.TicketSettle, PermissionKeys.MenuEdit, PermissionKeys.MenuEditPrices, PermissionKeys.ReportsOwn,
            PermissionKeys.ReportsAll, PermissionKeys.DayOpenClose, PermissionKeys.StaffManage, PermissionKeys.PermissionsManage,
            PermissionKeys.DevicesManage, PermissionKeys.SettingsManage, PermissionKeys.ErpManage);
        PermissionCatalog.All.Should().OnlyContain(p => p.Help.Length > 0 && p.Label.Length > 0);
    }

    [Theory]
    [InlineData(PermissionKeys.TicketSettle)]
    [InlineData(PermissionKeys.MenuEditPrices)]
    [InlineData(PermissionKeys.ReportsAll)]
    [InlineData(PermissionKeys.DayOpenClose)]
    [InlineData(PermissionKeys.SettingsManage)]
    public async Task Needs_approval_is_refused_for_keys_that_can_not_be_approved(string key)
    {
        var act = () => Save(("Waiter", key, PermissionValue.NeedsApproval));
        (await act.Should().ThrowAsync<FieldErrorsException>()).Which.Message.Should().Contain("can't be approved");
    }

    [Fact]
    public async Task The_admin_roles_permissions_manage_is_locked()
    {
        var act = () => Save(("Admin", PermissionKeys.PermissionsManage, PermissionValue.NotAllowed));
        (await act.Should().ThrowAsync<FieldErrorsException>()).Which.Message.Should().Be("Kept so you can't lock yourself out.");
    }

    [Theory]
    [InlineData(PermissionKeys.PermissionsManage)]
    [InlineData(PermissionKeys.StaffManage)]
    public async Task Saving_is_refused_when_no_active_user_would_keep_the_key(string key)
    {
        // Admin keeps permissions.manage (locked), so take it away by leaving the Admin role with nobody active.
        if (key == PermissionKeys.PermissionsManage)
            await F.Db.Users.Where(u => u.RoleId == Role("Admin")).ExecuteUpdateAsync(u => u.SetProperty(x => x.Active, false));
        var act = () => Save(EveryRole(key, PermissionValue.NotAllowed).Where(c => c.Item1 != "Admin" || key != PermissionKeys.PermissionsManage).ToArray());
        (await act.Should().ThrowAsync<FieldErrorsException>()).Which.Errors.Should().ContainKey(key);
    }

    [Fact]
    public async Task Saving_is_refused_when_nobody_can_approve_a_needs_approval_key()
    {
        var act = () => Save(EveryRole(PermissionKeys.OrderVoid, PermissionValue.NeedsApproval));
        (await act.Should().ThrowAsync<FieldErrorsException>()).Which.Errors[PermissionKeys.OrderVoid]
            .Should().Be("Nobody can approve \"Void a printed item\". Allow it for at least one role.");
    }

    [Fact]
    public async Task A_save_from_an_older_version_is_refused()
    {
        var old = await F.Get<GetPermissions>().Execute(None);
        await Save(("Waiter", PermissionKeys.OrderGift, PermissionValue.NeedsApproval));

        var act = () => F.Get<SavePermissions>().Execute(new SavePermissionsRequest(old.Version,
            [new PermissionCellChange(Role("Waiter"), PermissionKeys.OrderDiscount, PermissionValue.NeedsApproval)]), None);

        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(PermissionCodes.Conflict);
    }

    [Fact]
    public async Task Each_changed_cell_is_audited_announced_and_used_on_the_next_request()
    {
        var edna = await F.LoginOn("edna", "Pillar 1");
        (await F.Get<IPermissionService>().Check(edna, PermissionKeys.OrderGift)).Should().Be(PermissionValue.NotAllowed);
        await F.LoginOn("Administrator", "Counter A");
        F.Events.Published.Clear();

        var grid = await Save(
            ("Waiter", PermissionKeys.OrderGift, PermissionValue.NeedsApproval),
            ("Waiter", PermissionKeys.OrderAdd, PermissionValue.Allowed), // unchanged
            ("Cashier", PermissionKeys.TicketReprint, PermissionValue.NeedsApproval));

        var audits = F.Read().AuditEntries.Where(a => a.Action == AuditActions.PermissionChanged).ToList();
        audits.Should().HaveCount(2);
        audits.Should().Contain(a => a.NewValueJson!.Contains("order.gift") && a.OldValueJson!.Contains("NotAllowed"));
        F.Events.Published.Should().ContainSingle(e => e.Name == PosEventNames.PermissionsChanged);
        grid.Cells[Role("Waiter")][PermissionKeys.OrderGift].Should().Be(PermissionValue.NeedsApproval);
        (await F.Get<IPermissionService>().Check(edna, PermissionKeys.OrderGift)).Should().Be(PermissionValue.NeedsApproval);

        var history = await F.Get<PermissionsHistory>().Execute(None);
        history.Should().Contain(h => h.Role == "Waiter" && h.Key == PermissionKeys.OrderGift && h.Old == "NotAllowed" && h.New == "NeedsApproval"
            && h.Who == "Administrator");
    }

    [Fact]
    public async Task A_new_role_copies_its_permissions_and_its_holder_can_approve_what_it_allows()
    {
        var grid = await F.Get<RoleCommands>().Create(new CreateRoleRequest("Bar captain", Role("Waiter")), None);
        var captain = Role("Bar captain");
        grid.Cells[captain].Should().BeEquivalentTo(grid.Cells[Role("Waiter")]);
        grid.Roles.Select(r => r.Name).Should().Equal("Waiter", "Cashier", "Supervisor", "Admin", "Bar captain");
        await FluentActions.Awaiting(() => F.Get<RoleCommands>().Create(new CreateRoleRequest(" bar captain ", null), None))
            .Should().ThrowAsync<DomainException>();

        await Save(("Bar captain", PermissionKeys.OrderVoid, PermissionValue.Allowed));
        var otieno = F.User("edna");
        await F.Db.Users.Where(u => u.Id == otieno.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.RoleId, captain));
        var asCaptain = await F.LoginOn("edna", "Pillar 1");
        (await F.Get<IPermissionService>().Check(asCaptain, PermissionKeys.OrderVoid)).Should().Be(PermissionValue.Allowed);
    }

    [Fact]
    public async Task A_role_with_users_or_a_built_in_role_can_not_be_deleted_and_an_empty_one_can()
    {
        await F.Get<RoleCommands>().Create(new CreateRoleRequest("Bar captain", Role("Waiter")), None);
        var captain = Role("Bar captain");
        await F.Db.Users.Where(u => u.Name == "edna").ExecuteUpdateAsync(u => u.SetProperty(x => x.RoleId, captain));

        var inUse = () => F.Get<RoleCommands>().Delete(captain, None);
        (await inUse.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("edna has this role.");
        var builtIn = () => F.Get<RoleCommands>().Delete(Role("Waiter"), None);
        (await builtIn.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(PermissionCodes.BuiltInRole);

        await F.Db.Users.Where(u => u.Name == "edna").ExecuteUpdateAsync(u => u.SetProperty(x => x.RoleId, Role("Waiter")));
        await F.Get<RoleCommands>().Delete(captain, None);
        F.Read().Roles.Any(r => r.Id == captain).Should().BeFalse();
        F.Read().RolePermissions.Any(p => p.RoleId == captain).Should().BeFalse();
    }

    [Fact]
    public async Task Roles_and_permissions_need_permissions_manage()
    {
        await F.LoginOn("SAM", "Counter A");
        await FluentActions.Awaiting(() => F.Get<GetPermissions>().Execute(None)).Should().ThrowAsync<PermissionDeniedException>();
        await FluentActions.Awaiting(() => F.Get<RoleCommands>().Create(new CreateRoleRequest("X", null), None))
            .Should().ThrowAsync<PermissionDeniedException>();
    }
}
