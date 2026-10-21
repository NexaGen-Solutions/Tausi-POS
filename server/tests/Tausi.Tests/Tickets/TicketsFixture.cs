using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tausi.Application;
using Tausi.Application.Abstractions;
using Tausi.Application.Auth;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Domain.People;
using Tausi.Domain.Service;
using Tausi.Domain.Settings;
using Tausi.Infrastructure.Persistence;
using Tausi.Infrastructure.Seeding;
using Tausi.Tests.Support;

namespace Tausi.Tests.Tickets;

/// <summary>
/// Application services on SQLite with Mau's demo layout, menu, staff and terminals, an open day
/// and no tickets. <see cref="LoginOn"/> logs a person in on a terminal with a real session.
/// </summary>
public sealed class TicketsFixture : IAsyncDisposable
{
    private readonly TestDb _db = new();
    private readonly ServiceProvider _services;

    public FakeSession Session { get; } = new();
    public FakeApprovalSource Approval { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero));
    public RecordingEvents Events { get; } = new();
    public FastPinHasher Hasher { get; } = new();
    public Dictionary<string, Role> Roles { get; private set; } = [];

    private TicketsFixture()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(_db.Context);
        services.AddSingleton<ITausiDb>(_db.Context);
        services.AddSingleton<ITicketDatabase>(new TicketDatabase(_db.Context));
        services.AddSingleton<ICurrentSession>(Session);
        services.AddSingleton<IApprovalTokenSource>(Approval);
        services.AddSingleton<IPosEvents>(Events);
        services.AddSingleton<IPinHasher>(Hasher);
        _services = services.BuildServiceProvider();
    }

    public static async Task<TicketsFixture> Create(bool openDay = true)
    {
        var f = new TicketsFixture();
        f.Roles = await Builders.SeedRoles(f._db.Context);
        f._db.Context.BusinessSettings.Add(new BusinessSettings { Name = "Test", VatRate = 16m });
        await f._db.Context.SaveChangesAsync();
        await new Seeder(f._db.Context, f.Hasher, NullLogger<Seeder>.Instance).SeedDemo("pw", CancellationToken.None, filePrinters: true);
        // Start from no tickets; each test makes its own.
        await f._db.Context.OrderLines.ExecuteDeleteAsync();
        await f._db.Context.Tickets.ExecuteDeleteAsync();
        await f._db.Context.WorkPeriods.ExecuteDeleteAsync();
        if (openDay)
        {
            f._db.Context.WorkPeriods.Add(new WorkPeriod { OpenedAt = f.Clock.GetUtcNow(), OpenedByUserId = f.User("MAU").Id });
            await f._db.Context.SaveChangesAsync();
        }
        f._db.Context.ChangeTracker.Clear();
        await f.LoginOn("edna", "Counter A");
        return f;
    }

    public SqliteTestDbContext Db => _db.Context;
    public SqliteTestDbContext Read() => _db.NewContext();
    public SqliteTestDbContext NewContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) => _db.NewContext(interceptors);

    public T Get<T>() where T : notnull
    {
        _db.Context.ChangeTracker.Clear();
        return _services.CreateScope().ServiceProvider.GetRequiredService<T>();
    }

    public User User(string name) => Read().Users.Single(u => u.Name == name);
    public DiningTable Table(string name) => Read().Tables.Single(t => t.Name == name);
    public Ticket Ticket(long number) => Read().Tickets.Include(k => k.Lines).Single(k => k.Number == number);
    public Guid Item(string name) => Read().MenuItems.Single(i => i.Name == name).Id;

    /// <summary>Logs this person in on this terminal (a real server session) and acts as them.</summary>
    public async Task<SessionUser> LoginOn(string name, string terminalName)
    {
        var user = User(name);
        var role = Read().Roles.Single(r => r.Id == user.RoleId);
        var terminal = Read().Terminals.Single(t => t.Name == terminalName);
        var session = await Get<SessionKeeper>().Start(user.Id, terminal.Id, CancellationToken.None);
        await _db.Context.SaveChangesAsync();
        _db.Context.ChangeTracker.Clear();
        Session.User = new SessionUser(user.Id, user.Name, role.Id, role.Name, terminal.Id, terminal.Name, session.Id);
        Session.Terminal = new DeviceTerminal(terminal.Id, terminal.Name);
        Approval.ApprovalToken = null;
        return Session.User;
    }

    public void ActAs(SessionUser user)
    {
        Session.User = user;
        Session.Terminal = new DeviceTerminal(user.TerminalId!.Value, user.TerminalName!);
        Approval.ApprovalToken = null;
    }

    /// <summary>Saves a new ticket (its first lines) on a table as the current user.</summary>
    public async Task<TicketDetailDto> NewTicket(string? table, params (string Item, decimal Qty)[] lines)
    {
        var tableId = table is null ? (Guid?)null : Table(table).Id;
        var request = new StartTicketRequest(tableId, null,
            (lines.Length == 0 ? [("Tusker Lager", 1m)] : lines).Select(l => new NewLineRequest(Item(l.Item), l.Qty)).ToList());
        return await Get<StartTicket>().Execute(request, CancellationToken.None);
    }

    public async Task SetPermission(string role, string key, PermissionValue value)
    {
        var roleId = Roles[role].Id;
        await Db.RolePermissions.Where(p => p.RoleId == roleId && p.PermissionKey == key)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Value, value));
    }

    /// <summary>A supervisor's approval token for one action, as the approval pad would get it.</summary>
    public async Task<string> ApprovalToken(string permission, string approver = "MAU", TimeSpan? age = null)
    {
        var token = SecureTokens.New();
        var created = Clock.GetUtcNow() - (age ?? TimeSpan.Zero);
        Db.ApprovalGrants.Add(new ApprovalGrant
        {
            TokenHash = SecureTokens.Hash(token),
            PermissionKey = permission,
            ApprovedByUserId = User(approver).Id,
            CreatedAt = created,
            ExpiresAt = created + ApprovalGrant.Lifetime,
        });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return token;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _db.Dispose();
    }
}
