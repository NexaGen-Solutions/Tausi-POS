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

var builder = WebApplication.CreateBuilder(args);

// Settings and the dev data folder from before the rename to Tausi still work (warned once each).
LegacyNames.ApplyToConfiguration(builder.Configuration);
LegacyNames.CopyOldDataDirectory(
    builder.Configuration.GetSection(TausiOptions.Section).Get<TausiOptions>()?.DataDirectory ?? "data",
    builder.Environment.ContentRootPath);

// Runs as a Windows Service or a systemd service when started by one; does nothing otherwise.
builder.Host.UseWindowsService(o => o.ServiceName = "TausiPOSServer");
builder.Host.UseSystemd();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddTausiAuth(builder.Configuration, builder.Environment);
builder.Services.AddTausiCors();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemMapping>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<TerminalConnections>();
builder.Services.AddSingleton<IPosEvents, SignalRPosEvents>();
builder.Services.AddSingleton<ITerminalPrintChannel, HubTerminalPrintChannel>();
builder.Services.AddHostedService<DatabaseStartup>();

var app = builder.Build();
foreach (var warning in LegacyNames.TakeWarnings()) app.Logger.LogWarning("{Warning}", warning);

if (args.Length > 0 && await Commands.TryRun(app, args))
    return;

app.UseExceptionHandler();
app.UseTausiStaticFiles();
app.UseRouting();
app.UseCors();
app.UseMiddleware<DeviceTokenMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<PinChangeGate>();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapSystemEndpoints();
app.MapAuthEndpoints();
app.MapTerminalEndpoints();
app.MapSettingsEndpoints();
app.MapPrinterEndpoints();
app.MapDayEndpoints();
app.MapStaffEndpoints();
app.MapMenuEndpoints();
app.MapDevEndpoints();
app.MapHub<PosHub>(PosHub.Path);
app.MapTausiWebFallback();

app.Run();

/// <summary>Lets the integration tests start the app.</summary>
public partial class Program;
