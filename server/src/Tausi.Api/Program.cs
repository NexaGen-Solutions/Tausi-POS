using System.Text.Json.Serialization;
using Tausi.Api;
using Tausi.Api.Auth;
using Tausi.Api.Endpoints;
using Tausi.Api.Hubs;
using Tausi.Api.Platform;
using Tausi.Application;
using Tausi.Application.Events;
using Tausi.Application.Printing;
using Tausi.Infrastructure;
using Tausi.Infrastructure.Legacy;

// A crash before the logs are set up (an installer step run as SYSTEM, say) still leaves its reason.
AppDomain.CurrentDomain.UnhandledException += (_, e) => ServerHost.WriteCrash(e.ExceptionObject);

// An installed server reads its appsettings.json (and its relative folders) next to the program, from
// wherever it is started: the service, an installer step, a technician's command prompt.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = ServerHost.InstalledContentRoot() });

// An installed server's folders and settings file (C:\ProgramData\Tausi POS), log files and the Event Log.
ServerHost.Configure(builder);

// Settings and the dev data folder from before the rename to Tausi still work (warned once each).
LegacyNames.ApplyToConfiguration(builder.Configuration);
LegacyNames.CopyOldDataDirectory(
    builder.Configuration.GetSection(TausiOptions.Section).Get<TausiOptions>()?.DataDirectory ?? "data",
    builder.Environment.ContentRootPath);

// Runs as a Windows Service or a systemd service when started by one; does nothing otherwise.
builder.Host.UseWindowsService(o => o.ServiceName = ServerHost.ServiceName);
builder.Host.UseSystemd();

// The tests' licence key is trusted only in Development.
builder.Services.AddSingleton(new Tausi.Application.Setup.LicenceOptions(builder.Environment.IsDevelopment()));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddTausiAuth(builder.Configuration, builder.Environment);
builder.Services.AddTausiCors();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemMapping>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHostedService<TerminalPresenceMonitor>();
builder.Services.AddSingleton<IPosEvents, SignalRPosEvents>();
builder.Services.AddSingleton<ITerminalPrintChannel, HubTerminalPrintChannel>();
builder.Services.AddHostedService<DatabaseStartup>();
builder.Services.AddSingleton<Tausi.Application.Updates.IUpdateInstaller, UpdateInstaller>();

var app = builder.Build();
foreach (var warning in LegacyNames.TakeWarnings()) app.Logger.LogWarning("{Warning}", warning);

if (args.Length > 0 && await Commands.TryRun(app, args))
    return;

app.UseExceptionHandler();
app.UseMiddleware<ContentSecurityPolicy>();
app.UseTausiStaticFiles();
app.UseRouting();
// While a backup is being restored the API answers 503.
app.UseMiddleware<MaintenanceMode>();
// The server's version on every answer; terminals too old to work with it update before login.
app.UseMiddleware<VersionCheck>();
// A new install answers only the setup wizard until it is set up.
app.UseMiddleware<SetupMode>();
app.UseCors();
app.UseMiddleware<DeviceTokenMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<PinChangeGate>();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapSystemEndpoints();
app.MapSetupEndpoints();
app.MapLicenceEndpoints();
app.MapAuthEndpoints();
app.MapTerminalEndpoints();
app.MapSettingsEndpoints();
app.MapPrinterEndpoints();
app.MapDayEndpoints();
app.MapStaffEndpoints();
app.MapPermissionEndpoints();
app.MapStockEndpoints();
app.MapMoneyEndpoints();
app.MapReportEndpoints();
app.MapBackupEndpoints();
app.MapUpdateEndpoints();
app.MapMenuEndpoints();
app.MapTicketEndpoints();
app.MapHub<PosHub>(PosHub.Path);
app.MapTausiWebFallback();

app.Run();

/// <summary>Lets the integration tests start the app.</summary>
public partial class Program;
