using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tausi.Application;
using Tausi.Application.Abstractions;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.Devices;
using Tausi.Domain.Menu;
using Tausi.Domain.People;
using Tausi.Domain.Settings;
using Tausi.Infrastructure.Seeding;
using Tausi.Tests.Support;

namespace Tausi.Tests.Menu;

/// <summary>The real Application services on SQLite with the demo menu, a fake session, clock and events.</summary>
public sealed class MenuFixture : IAsyncDisposable
{
    private readonly TestDb _db = new();
    private readonly ServiceProvider _services;
    private readonly FakeApprovalSource _approval = new();

    public FakeSession Session { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    public RecordingEvents Events { get; } = new();
    public FastPinHasher Hasher { get; } = new();
    public Dictionary<string, Role> Roles { get; private set; } = [];
    public Dictionary<string, User> Users { get; } = [];
    public Terminal CounterA { get; private set; } = null!;
    public Terminal CounterB { get; private set; } = null!;

    private MenuFixture()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(_db.Context);
        services.AddSingleton<ITausiDb>(_db.Context);
        services.AddSingleton<ICurrentSession>(Session);
        services.AddSingleton<IApprovalTokenSource>(_approval);
        services.AddSingleton<IPosEvents>(Events);
        services.AddSingleton<IPinHasher>(Hasher);
        _services = services.BuildServiceProvider();
    }

    public static async Task<MenuFixture> Create(bool demoMenu = true)
    {
        var f = new MenuFixture();
        f.Roles = await Builders.SeedRoles(f._db.Context);
        f._db.Context.BusinessSettings.Add(new BusinessSettings { Name = "Test", VatRate = 16m });
        await f._db.Context.SaveChangesAsync();
        var seeder = new Seeder(f._db.Context, f.Hasher, NullLogger<Seeder>.Instance);
        await seeder.SeedBase(CancellationToken.None);
        if (demoMenu) await seeder.SeedDemoMenu(CancellationToken.None);
        f.CounterA = await Builders.AddTerminal(f._db.Context, "Counter A");
        f.CounterB = await Builders.AddTerminal(f._db.Context, "Counter B");
        f.Users["Administrator"] = await Builders.AddUser(f._db.Context, f.Hasher, "Administrator", f.Roles["Admin"], "7031");
        f.Users["MAU"] = await Builders.AddUser(f._db.Context, f.Hasher, "MAU", f.Roles["Supervisor"], "8014");
        f.ActAs("Administrator");
        return f;
    }

    public SqliteTestDbContext Db => _db.Context;
    public SqliteTestDbContext Read() => _db.NewContext();

    public void ActAs(string name, Terminal? terminal = null)
    {
        var user = Users[name];
        Session.User = Builders.Session(user, Roles.Values.Single(r => r.Id == user.RoleId), terminal);
        Session.Terminal = terminal is null ? null : new DeviceTerminal(terminal.Id, terminal.Name);
    }

    public void OnTerminal(Terminal terminal) => Session.Terminal = new DeviceTerminal(terminal.Id, terminal.Name);

    public string? ApprovalToken { set => _approval.ApprovalToken = value; }

    public T Get<T>() where T : notnull
    {
        _db.Context.ChangeTracker.Clear();
        return _services.CreateScope().ServiceProvider.GetRequiredService<T>();
    }

    public async Task SetPermission(string role, string key, PermissionValue value)
    {
        var cell = await Db.RolePermissions.SingleAsync(p => p.RoleId == Roles[role].Id && p.PermissionKey == key);
        cell.Value = value;
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    public MenuItem Item(string name) => Read().MenuItems.Include(i => i.Prices).Single(i => i.Name == name);
    public PriceList List(string name) => Read().PriceLists.Single(l => l.Name == name);
    public Category Category(string name) => Read().Categories.Single(c => c.Name == name);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _db.Dispose();
    }
}
