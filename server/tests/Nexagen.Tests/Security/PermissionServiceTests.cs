using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Nexagen.Application.Security;
using Nexagen.Domain.People;
using Nexagen.Tests.Support;

namespace Nexagen.Tests.Security;

public class PermissionServiceTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 19, 42, 0, TimeSpan.Zero));
    private readonly FastPinHasher _hasher = new();
    private PermissionService _permissions = null!;
    private Dictionary<string, Role> _roles = null!;
    private SessionUser _waiter = null!;
    private SessionUser _cashier = null!;
    private User _supervisor = null!;

    public async Task InitializeAsync()
    {
        _roles = await Builders.SeedRoles(_db.Context);
        var edna = await Builders.AddUser(_db.Context, _hasher, "edna", _roles["Waiter"], "1111");
        var carol = await Builders.AddUser(_db.Context, _hasher, "CAROL", _roles["Cashier"], "2222");
        _supervisor = await Builders.AddUser(_db.Context, _hasher, "MAU", _roles["Supervisor"], "3333");
        _waiter = Builders.Session(edna, _roles["Waiter"]);
        _cashier = Builders.Session(carol, _roles["Cashier"]);
        _permissions = new PermissionService(_db.Context, _clock);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> Approve(string key, User approver, TimeSpan? lifetime = null)
    {
        var token = SecureTokens.New();
        _db.Context.ApprovalGrants.Add(new ApprovalGrant
        {
            TokenHash = SecureTokens.Hash(token),
            PermissionKey = key,
            ApprovedByUserId = approver.Id,
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow() + (lifetime ?? ApprovalGrant.Lifetime),
        });
        await _db.Context.SaveChangesAsync();
        return token;
    }

    [Fact]
    public async Task Check_reads_the_role_grid()
    {
        (await _permissions.Check(_waiter, PermissionKeys.OrderVoid)).Should().Be(PermissionValue.NeedsApproval);
        (await _permissions.Check(_waiter, PermissionKeys.TicketSettle)).Should().Be(PermissionValue.NotAllowed);
        (await _permissions.Check(_cashier, PermissionKeys.TicketSettle)).Should().Be(PermissionValue.Allowed);
    }

    [Fact]
    public async Task Grid_is_data_so_changing_a_cell_changes_the_answer()
    {
        var cell = await _db.Context.RolePermissions.SingleAsync(p => p.RoleId == _waiter.RoleId && p.PermissionKey == PermissionKeys.OrderVoid);
        cell.Value = PermissionValue.Allowed;
        await _db.Context.SaveChangesAsync();

        (await _permissions.Require(_waiter, PermissionKeys.OrderVoid)).WasApproved.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_cell_means_not_allowed()
    {
        await _db.Context.RolePermissions.Where(p => p.PermissionKey == PermissionKeys.OrderAdd).ExecuteDeleteAsync();

        (await _permissions.Check(_cashier, PermissionKeys.OrderAdd)).Should().Be(PermissionValue.NotAllowed);
    }

    [Fact]
    public async Task Unknown_key_is_a_programming_error()
    {
        var act = () => _permissions.Check(_waiter, "order.teleport");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Not_allowed_throws_permission_denied()
    {
        var act = () => _permissions.Require(_waiter, PermissionKeys.TicketSettle);
        (await act.Should().ThrowAsync<PermissionDeniedException>()).Which.PermissionKey.Should().Be("ticket.settle");
    }

    [Fact]
    public async Task Needs_approval_without_a_token_throws_approval_required()
    {
        var act = () => _permissions.Require(_waiter, PermissionKeys.OrderVoid);
        (await act.Should().ThrowAsync<ApprovalRequiredException>()).Which.PermissionKey.Should().Be("order.void");
    }

    [Fact]
    public async Task Valid_approval_succeeds_once_and_names_the_approver()
    {
        var token = await Approve(PermissionKeys.OrderVoid, _supervisor);

        var grant = await _permissions.Require(_waiter, PermissionKeys.OrderVoid, token);
        grant.ApprovedByUserId.Should().Be(_supervisor.Id);

        var reuse = () => _permissions.Require(_waiter, PermissionKeys.OrderVoid, token);
        await reuse.Should().ThrowAsync<ApprovalRequiredException>().WithMessage("*already used*");

        var stored = await _db.NewContext().ApprovalGrants.SingleAsync();
        stored.UsedByUserId.Should().Be(_waiter.UserId);
    }

    [Fact]
    public async Task Approval_expires_after_60_seconds()
    {
        var token = await Approve(PermissionKeys.OrderVoid, _supervisor);
        _clock.Advance(TimeSpan.FromSeconds(61));

        var act = () => _permissions.Require(_waiter, PermissionKeys.OrderVoid, token);
        await act.Should().ThrowAsync<ApprovalRequiredException>();
    }

    [Fact]
    public async Task Approval_for_another_permission_does_not_count()
    {
        var token = await Approve(PermissionKeys.TicketReprint, _supervisor);

        var act = () => _permissions.Require(_waiter, PermissionKeys.OrderVoid, token);
        await act.Should().ThrowAsync<ApprovalRequiredException>();
    }

    [Fact]
    public async Task Approver_must_still_be_allowed()
    {
        var token = await Approve(PermissionKeys.OrderVoid, _supervisor);
        var cell = await _db.Context.RolePermissions.SingleAsync(p => p.RoleId == _supervisor.RoleId && p.PermissionKey == PermissionKeys.OrderVoid);
        cell.Value = PermissionValue.NeedsApproval;
        await _db.Context.SaveChangesAsync();

        var act = () => _permissions.Require(_waiter, PermissionKeys.OrderVoid, token);
        await act.Should().ThrowAsync<ApprovalRequiredException>();
    }

    [Fact]
    public async Task Approval_does_not_unlock_a_not_allowed_permission()
    {
        var token = await Approve(PermissionKeys.TicketSettle, _supervisor);

        var act = () => _permissions.Require(_waiter, PermissionKeys.TicketSettle, token);
        await act.Should().ThrowAsync<PermissionDeniedException>();
    }

    [Fact]
    public async Task Garbage_token_is_refused()
    {
        var act = () => _permissions.Require(_waiter, PermissionKeys.OrderVoid, "not-a-token");
        await act.Should().ThrowAsync<ApprovalRequiredException>();
    }
}
