using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tausi.Application;
using Tausi.Application.Abstractions;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.Devices;
using Tausi.Domain.People;
using Tausi.Tests.Support;

namespace Tausi.Tests.Staff;

/// <summary>The real Application services on SQLite, with a fake session, clock and events.</summary>
public sealed class StaffFixture : IAsyncDisposable
{
    private readonly TestDb _db = new();
    private readonly ServiceProvider _services;

    public FakeSession Session { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    public RecordingEvents Events { get; } = new();
    public FastPinHasher Hasher { get; } = new();
    public Dictionary<string, Role> Roles { get; private set; } = [];
    public Dictionary<string, User> Users { get; } = [];
    public Terminal Terminal { get; private set; } = null!;

    public StaffFixture()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(_db.Context);
        services.AddSingleton<ITausiDb>(_db.Context);
        services.AddSingleton<ICurrentSession>(Session);
        services.AddSingleton<IApprovalTokenSource>(new FakeApprovalSource());
        services.AddSingleton<IPosEvents>(Events);
        services.AddSingleton<IPinHasher>(Hasher);
        _services = services.BuildServiceProvider();
    }

    public static async Task<StaffFixture> Create()
    {
        var f = new StaffFixture();
        f.Roles = await Builders.SeedRoles(f._db.Context);
        f.Terminal = await Builders.AddTerminal(f._db.Context);
        foreach (var (name, role, pin) in new[]
        {
            ("Administrator", "Admin", "7031"), ("MAU", "Supervisor", "8014"), ("CAROL", "Cashier", "5739"),
            ("edna", "Waiter", "4821"), ("Ruth", "Waiter", "2365"),
        })
            f.Users[name] = await Builders.AddUser(f._db.Context, f.Hasher, name, f.Roles[role], pin);
        f.ActAs("Administrator");
        return f;
    }

    public SqliteTestDbContext Read() => _db.NewContext();

    public SqliteTestDbContext Db => _db.Context;

    public void ActAs(string name)
    {
        var user = Users[name];
        Session.User = Builders.Session(user, Roles.Values.Single(r => r.Id == user.RoleId));
    }

    /// <summary>A fresh scope, so each call sees the database as saved.</summary>
    public T Get<T>() where T : notnull
    {
        _db.Context.ChangeTracker.Clear();
        return _services.CreateScope().ServiceProvider.GetRequiredService<T>();
    }

    public long Version(string name) => Read().Users.Single(u => u.Name == name).RowVersion;

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _db.Dispose();
    }
}
