using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Files;
using Nexagen.Application.Ledger;
using Nexagen.Application.Printing;
using Nexagen.Application.Security;
using Nexagen.Infrastructure.Files;
using Nexagen.Infrastructure.Ledger;
using Nexagen.Infrastructure.Persistence;
using Nexagen.Infrastructure.Platform;
using Nexagen.Infrastructure.Printing;
using Nexagen.Infrastructure.Security;
using Nexagen.Infrastructure.Seeding;

namespace Nexagen.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<NexagenOptions>(config.GetSection(NexagenOptions.Section));
        services.Configure<DatabaseOptions>(config.GetSection("Database"));

        var provider = config.GetSection("Database").Get<DatabaseOptions>()?.Provider ?? DatabaseProvider.Postgres;
        var connection = config.GetConnectionString("Nexagen")
            ?? throw new InvalidOperationException("ConnectionStrings:Nexagen is not set.");
        switch (provider)
        {
            case DatabaseProvider.SqlServer:
                services.AddDbContext<SqlServerNexagenDbContext>(o => o.UseSqlServer(connection));
                services.AddScoped<NexagenDbContext>(sp => sp.GetRequiredService<SqlServerNexagenDbContext>());
                break;
            default:
                services.AddDbContext<PostgresNexagenDbContext>(o => o.UseNpgsql(connection));
                services.AddScoped<NexagenDbContext>(sp => sp.GetRequiredService<PostgresNexagenDbContext>());
                break;
        }
        services.AddScoped<INexagenDb>(sp => sp.GetRequiredService<NexagenDbContext>());

        services.AddSingleton<ServerSecrets>();
        services.AddSingleton<IPinHasher, PinHasher>();
        services.AddScoped<ILedgerPostingService, LedgerPostingService>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddScoped<Seeder>();

        services.AddScoped<IPrinterTransport, Network9100Transport>();
        services.AddScoped<IPrinterTransport, WindowsShareTransport>();
        services.AddScoped<IPrinterTransport, ViaTerminalTransport>();
        services.AddScoped<IPrinterTransport, FilePrinterTransport>();
        services.AddScoped<PrintJobProcessor>();
        services.AddScoped<ILogoProvider, SkiaLogoProvider>();
        services.AddHostedService<PrintDispatcher>();
        services.AddHostedService<HousekeepingService>();
        return services;
    }
}
