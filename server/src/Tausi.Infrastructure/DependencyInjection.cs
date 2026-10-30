using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tausi.Application.Abstractions;
using Tausi.Application.Files;
using Tausi.Application.Ledger;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Infrastructure.Files;
using Tausi.Infrastructure.Ledger;
using Tausi.Infrastructure.Persistence;
using Tausi.Infrastructure.Platform;
using Tausi.Infrastructure.Printing;
using Tausi.Infrastructure.Security;
using Tausi.Infrastructure.Seeding;

namespace Tausi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<TausiOptions>(config.GetSection(TausiOptions.Section));
        services.Configure<DatabaseOptions>(config.GetSection("Database"));

        // Queries load related collections in one SQL statement (EF's default, stated so it doesn't warn);
        // the table map relies on it.
        var provider = config.GetSection("Database").Get<DatabaseOptions>()?.Provider ?? DatabaseProvider.Postgres;
        var connection = Platform.MachineSecrets.UnprotectConnectionString(config.GetConnectionString(TausiOptions.ConnectionStringName)
            ?? throw new InvalidOperationException("ConnectionStrings:Tausi is not set."));
        services.AddScoped<IdempotencyMark>();
        switch (provider)
        {
            case DatabaseProvider.SqlServer:
                services.AddDbContext<SqlServerTausiDbContext>((sp, o) => o.UseSqlServer(connection, x => x.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery))
                    .AddInterceptors(sp.GetRequiredService<IdempotencyMark>()));
                services.AddScoped<TausiDbContext>(sp => sp.GetRequiredService<SqlServerTausiDbContext>());
                break;
            default:
                services.AddDbContext<PostgresTausiDbContext>((sp, o) => o.UseNpgsql(connection, x => x.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery))
                    .AddInterceptors(sp.GetRequiredService<IdempotencyMark>()));
                services.AddScoped<TausiDbContext>(sp => sp.GetRequiredService<PostgresTausiDbContext>());
                break;
        }
        services.AddScoped<ITausiDb>(sp => sp.GetRequiredService<TausiDbContext>());

        services.AddSingleton<ServerSecrets>();
        services.AddSingleton<IPinHasher, PinHasher>();
        services.AddScoped<ILedgerPostingService, LedgerPostingService>();
        services.AddScoped<ITicketDatabase>(sp => new TicketDatabase(sp.GetRequiredService<TausiDbContext>()));
        services.AddScoped<Application.Stock.IStockDatabase>(sp => new StockDatabase(sp.GetRequiredService<TausiDbContext>()));
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<Application.Setup.ISetupTokenFile, Platform.SetupTokenFile>();
        services.AddScoped<Application.Setup.ISambaPosSource, Import.SambaPosReader>();
        services.AddScoped<Seeder>();

        services.AddScoped<IPrinterTransport, Network9100Transport>();
        services.AddScoped<IPrinterTransport, WindowsShareTransport>();
        services.AddScoped<IPrinterTransport, ViaTerminalTransport>();
        services.AddScoped<IPrinterTransport, FilePrinterTransport>();
        services.AddScoped<PrintJobProcessor>();
        services.AddScoped<ILogoProvider, SkiaLogoProvider>();
        services.AddScoped<ILogoImages, SkiaLogoImages>();
        services.AddScoped<IPrinterChecker, PrinterChecker>();
        services.AddHostedService<PrintDispatcher>();
        services.AddHostedService<PrinterHealthMonitor>();
        services.AddHostedService<HousekeepingService>();
        services.AddHostedService<TicketLockSweeper>();

        services.AddSingleton<Backups.BackupEngine>();
        services.AddSingleton<Application.Backups.IBackupEngine>(sp => sp.GetRequiredService<Backups.BackupEngine>());
        services.AddHostedService<Backups.BackupScheduler>();
        services.AddSingleton<Application.Updates.IUpdateSource, Updates.HttpUpdateSource>();
        services.AddSingleton<Application.Updates.ITerminalUpdateFiles, Updates.TerminalUpdateFiles>();
        services.AddSingleton<Application.Terminals.IServerProbe, Updates.ServerProbe>();
        return services;
    }
}
