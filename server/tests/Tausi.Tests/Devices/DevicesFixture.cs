using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tausi.Application;
using Tausi.Application.Abstractions;
using Tausi.Application.Events;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Domain.Devices;
using Tausi.Domain.People;
using Tausi.Domain.Settings;
using Tausi.Infrastructure.Seeding;
using Tausi.Tests.Support;

namespace Tausi.Tests.Devices;

/// <summary>Application services on SQLite with the demo terminals and printers, as Administrator on the admin site.</summary>
public sealed class DevicesFixture : IAsyncDisposable
{
    private readonly TestDb _db = new();
    private readonly ServiceProvider _services;

    public FakeSession Session { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    public RecordingEvents Events { get; } = new();
    public FastPinHasher Hasher { get; } = new();
    public User Admin { get; private set; } = null!;

    private DevicesFixture(bool development)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Tausi.Application.Common.DevelopmentHost(development));
        services.AddApplication();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(_db.Context);
        services.AddSingleton<ITausiDb>(_db.Context);
        services.AddSingleton<ICurrentSession>(Session);
        services.AddSingleton<IApprovalTokenSource>(new FakeApprovalSource());
        services.AddSingleton<IPosEvents>(Events);
        services.AddSingleton<IPinHasher>(Hasher);
        services.AddSingleton<IPrinterChecker, NoChecker>();
        _services = services.BuildServiceProvider();
    }

    /// <param name="development">As a Development server (the File printer is allowed there only).</param>
    public static async Task<DevicesFixture> Create(bool development = false)
    {
        var f = new DevicesFixture(development);
        var roles = await Builders.SeedRoles(f._db.Context);
        f._db.Context.BusinessSettings.Add(new BusinessSettings { Name = "Test" });
        await f._db.Context.SaveChangesAsync();
        await new Seeder(f._db.Context, f.Hasher, NullLogger<Seeder>.Instance).SeedDemo("pw", CancellationToken.None, filePrinters: true);
        f.Admin = f._db.Context.Users.Single(u => u.Name == "Administrator");
        f.Session.User = Builders.Session(f.Admin, roles["Admin"]);
        return f;
    }

    public SqliteTestDbContext Db => _db.Context;
    public SqliteTestDbContext Read() => _db.NewContext();

    public T Get<T>() where T : notnull
    {
        _db.Context.ChangeTracker.Clear();
        return _services.CreateScope().ServiceProvider.GetRequiredService<T>();
    }

    public Terminal Terminal(string name) => Read().Terminals.Single(t => t.Name == name);
    public Printer Printer(string name) => Read().Printers.Single(p => p.Name == name);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _db.Dispose();
    }

    private sealed class NoChecker : IPrinterChecker
    {
        public Task Check(PrinterConnection mode, string address, Guid? owningTerminalId, CancellationToken ct) => Task.CompletedTask;
    }
}
