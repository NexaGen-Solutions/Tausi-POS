using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Settings;
using Tausi.Application.Tickets;
using Tausi.Domain.People;
using Tausi.Infrastructure.Seeding;

namespace Tausi.Tests.Tickets;

/// <summary>
/// ticket.others: a waiter works only on the tickets they opened and starts tickets only on free
/// tables or their own; others' tickets open read-only, or not at all with pos.showOthersTickets off.
/// </summary>
public class TicketOwnershipTests : IAsyncLifetime
{
    private TicketsFixture _f = null!;

    public async Task InitializeAsync()
    {
        _f = await TicketsFixture.Create();
        await _f.SetPermission("Waiter", PermissionKeys.TicketOthers, PermissionValue.NotAllowed);
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task<TicketLockState> Lock(Guid id) => _f.Get<LockTicket>().Execute(id, CancellationToken.None);

    private async Task<TicketDetailDto> EdnasTicketOnTable4()
    {
        var ticket = await _f.NewTicket("4");
        await _f.Get<UnlockTicket>().Execute(ticket.Id, CancellationToken.None);
        return ticket;
    }

    [Fact]
    public async Task Another_waiters_ticket_opens_read_only_and_nothing_on_it_can_be_changed()
    {
        var ticket = await EdnasTicketOnTable4();
        await _f.LoginOn("Ruth", "Pillar 1");

        var state = await Lock(ticket.Id);
        state.Editable.Should().BeFalse();
        state.Message.Should().Be($"#{ticket.Number} is edna's ticket. You can view it but not change it.");
        (await _f.Get<GetTicket>().Execute(ticket.Id, CancellationToken.None)).Lines.Should().HaveCount(1, "she can still see it");

        var add = () => _f.Add(ticket.Id, "Sodas 300ml");
        (await add.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be(TicketCodes.NotYours);
        _f.Ticket(ticket.Number).Lines.Should().HaveCount(1);
    }

    [Fact]
    public async Task One_waiter_per_table_but_supervisors_and_cashiers_work_on_any_ticket()
    {
        var ticket = await EdnasTicketOnTable4();
        await _f.LoginOn("Ruth", "Pillar 1");

        var start = () => _f.NewTicket("4");
        (await start.Should().ThrowAsync<ForbiddenException>()).Which.Message.Should().Be("4 is edna's table. Ask edna or a supervisor.");
        var mine = await _f.NewTicket("5");
        var move = () => _f.Get<MoveTicket>().Execute(mine.Id, new MoveTicketRequest(_f.Table("4").Id), CancellationToken.None);
        (await move.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be(TicketCodes.TableTaken);

        await _f.LoginOn("SAM", "Counter B");
        (await Lock(ticket.Id)).Editable.Should().BeTrue();
        await _f.Add(ticket.Id, "Sodas 300ml");
        await _f.LoginOn("CAROL", "Counter B");
        (await Lock(ticket.Id)).Editable.Should().BeTrue("the cashier settles everyone's tickets");
    }

    [Fact]
    public async Task With_others_hidden_a_waiter_sees_only_that_the_table_is_taken()
    {
        var ticket = await EdnasTicketOnTable4();
        await _f.SetSetting(SettingsRegistry.PosShowOthersTickets, false);
        await _f.LoginOn("Ruth", "Pillar 1");

        var map = await _f.Get<GetTableMap>().Execute(null, CancellationToken.None);
        var taken = map.Tables.Single(t => t.Name == "4").Tickets.Single();
        taken.Should().Match<TicketSummaryDto>(k => k.Private && k.OpenedBy == "" && k.Total == 0 && k.Lock == null);
        var open = () => _f.Get<GetTicket>().Execute(ticket.Id, CancellationToken.None);
        await open.Should().ThrowAsync<ForbiddenException>();
        var lockIt = () => Lock(ticket.Id);
        await lockIt.Should().ThrowAsync<ForbiddenException>();

        await _f.LoginOn("SAM", "Counter B");
        var seen = (await _f.Get<GetTableMap>().Execute(null, CancellationToken.None)).Tables.Single(t => t.Name == "4").Tickets.Single();
        seen.Should().Match<TicketSummaryDto>(k => !k.Private && k.OpenedBy == "edna" && k.Total > 0);
    }

    [Fact]
    public async Task The_upgrade_adds_the_key_to_every_role_and_roles_a_business_made_keep_working_on_any_ticket()
    {
        await using var install = await TicketsFixture.CreateEmpty();
        var role = new Role { Name = "Head waiter" };
        role.Permissions.Add(new RolePermission { PermissionKey = PermissionKeys.OrderAdd, Value = PermissionValue.Allowed });
        install.Db.Roles.Add(role);
        // An install from before ticket.others: the built-in roles don't have it either.
        install.Db.RolePermissions.RemoveRange(install.Db.RolePermissions.Where(p => p.PermissionKey == PermissionKeys.TicketOthers));
        await install.Db.SaveChangesAsync();
        install.Db.ChangeTracker.Clear();

        await new Seeder(install.Db, install.Hasher, NullLogger<Seeder>.Instance, clock: install.Clock).SeedBase(CancellationToken.None);

        var permissions = install.Read().RolePermissions.Where(p => p.PermissionKey == PermissionKeys.TicketOthers).ToList();
        permissions.Single(p => p.RoleId == role.Id).Value.Should().Be(PermissionValue.Allowed);
        var waiter = install.Read().Roles.Single(r => r.Name == DefaultPermissions.Waiter);
        permissions.Single(p => p.RoleId == waiter.Id).Value.Should().Be(PermissionValue.NotAllowed, "the built-in default");
        DefaultPermissions.For(DefaultPermissions.Cashier, PermissionKeys.TicketOthers).Should().Be(PermissionValue.Allowed);
    }
}
