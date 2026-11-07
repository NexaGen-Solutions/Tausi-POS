using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

namespace Tausi.Relay;

/// <summary>The relay's settings (section "Relay").</summary>
public sealed class RelayOptions
{
    public const string Section = "Relay";

    /// <summary>The address Safaricom calls, e.g. https://pay.nexagensolutions.com.</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>Safaricom's published callback addresses (single addresses or ranges such as 196.201.214.0/24).</summary>
    public List<string> SafaricomAddresses { get; set; } = [];

    /// <summary>The reverse proxy in front (nginx on the same host): its X-Forwarded-For is believed.</summary>
    public List<string> TrustedProxies { get; set; } = ["127.0.0.1", "::1"];

    public string AdminUser { get; set; } = "nexagen";

    /// <summary>From <c>TausiRelay hash-password</c>; empty means the admin page can't be signed in to.</summary>
    public string AdminPasswordHash { get; set; } = "";

    /// <summary>Days an acknowledged payment is kept.</summary>
    public int KeepDays { get; set; } = 30;

    public IReadOnlyList<AddressRange> SafaricomRanges => _ranges ??= SafaricomAddresses.Select(AddressRange.Parse).ToList();

    private IReadOnlyList<AddressRange>? _ranges;
}

/// <summary>Builds the relay: PostgreSQL (or a database the tests give), callbacks, delivery, the admin page and /health.</summary>
public static class RelayHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.Section));
        builder.Services.TryAddSingletonTimeProvider();
        builder.Services.AddSingleton<Notifier>();
        builder.Services.AddScoped<Store>();
        builder.Services.AddHostedService<Cleanup>();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication(Admin.Scheme).AddCookie(Admin.Scheme, o =>
        {
            o.LoginPath = "/admin/login";
            o.Cookie.Name = "tausi_relay";
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
        });
        builder.Services.AddAuthorization();
        var connection = builder.Configuration.GetConnectionString("Relay");
        if (!string.IsNullOrEmpty(connection)) builder.Services.AddDbContext<RelayDb>(o => o.UseNpgsql(connection));
        configure?.Invoke(builder);

        var app = builder.Build();
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RelayOptions>>().Value;
        var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var proxy in options.TrustedProxies) forwarded.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
        app.UseForwardedHeaders(forwarded);
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapGet("/health", async (RelayDb db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));
        app.MapCallbacks();
        app.MapDelivery();
        app.MapAdmin();
        return app;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(s => s.ServiceType != typeof(TimeProvider))) services.AddSingleton(TimeProvider.System);
    }

    /// <summary>Brings the database up to date (PostgreSQL migrations; a test database is created as it is).</summary>
    public static async Task Prepare(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDb>();
        if (db.Database.IsNpgsql()) await db.Database.MigrateAsync();
        else await db.Database.EnsureCreatedAsync();
    }
}
