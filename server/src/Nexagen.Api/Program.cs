using System.Text.Json.Serialization;
using Nexagen.Api;
using Nexagen.Api.Auth;
using Nexagen.Api.Endpoints;
using Nexagen.Api.Hubs;
using Nexagen.Api.Platform;
using Nexagen.Application;
using Nexagen.Application.Events;
using Nexagen.Application.Printing;
using Nexagen.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Runs as a Windows Service or a systemd service when started by one; does nothing otherwise.
builder.Host.UseWindowsService(o => o.ServiceName = "Nexagen Server");
builder.Host.UseSystemd();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddNexagenAuth(builder.Configuration, builder.Environment);
builder.Services.AddNexagenCors();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemMapping>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<TerminalConnections>();
builder.Services.AddSingleton<IPosEvents, SignalRPosEvents>();
builder.Services.AddSingleton<ITerminalPrintChannel, HubTerminalPrintChannel>();
builder.Services.AddHostedService<DatabaseStartup>();

var app = builder.Build();

if (args.Length > 0 && await Commands.TryRun(app, args))
    return;

app.UseExceptionHandler();
app.UseNexagenStaticFiles();
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
app.MapDevEndpoints();
app.MapHub<PosHub>(PosHub.Path);
app.MapNexagenWebFallback();

app.Run();

/// <summary>Lets the integration tests start the app.</summary>
public partial class Program;
