using FluentAssertions;
using Tausi.Domain.People;
using Tausi.Infrastructure.Seeding;
using static Tausi.Domain.People.PermissionValue;

namespace Tausi.Tests.Security;

public class DefaultPermissionsTests
{
    [Fact]
    public void Every_permission_key_appears_exactly_once_in_the_grid()
    {
        DefaultPermissions.CoveredKeys.Should().BeEquivalentTo(PermissionKeys.All).And.OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("order.void", NeedsApproval, NeedsApproval, Allowed, Allowed)]
    [InlineData("order.gift", NotAllowed, NeedsApproval, Allowed, Allowed)]
    [InlineData("ticket.reprint", NeedsApproval, Allowed, Allowed, Allowed)]
    [InlineData("ticket.settle", NotAllowed, Allowed, Allowed, Allowed)]
    [InlineData("order.change_price", NotAllowed, NotAllowed, NeedsApproval, Allowed)]
    [InlineData("stock.receive", NotAllowed, NotAllowed, Allowed, Allowed)]
    [InlineData("settings.manage", NotAllowed, NotAllowed, NotAllowed, Allowed)]
    [InlineData("order.add", Allowed, Allowed, Allowed, Allowed)]
    public void Matches_the_agreed_defaults(string key, PermissionValue waiter, PermissionValue cashier, PermissionValue supervisor, PermissionValue admin)
    {
        DefaultPermissions.For("Waiter", key).Should().Be(waiter);
        DefaultPermissions.For("Cashier", key).Should().Be(cashier);
        DefaultPermissions.For("Supervisor", key).Should().Be(supervisor);
        DefaultPermissions.For("Admin", key).Should().Be(admin);
    }

    [Fact]
    public void Only_the_cashier_and_above_settle()
    {
        DefaultPermissions.For("Waiter", PermissionKeys.TicketSettle).Should().Be(NotAllowed);
    }
}
