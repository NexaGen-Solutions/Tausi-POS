using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Tausi.Api.Auth;
using Tausi.Api.Platform;
using Tausi.Application.Settings;
using Tausi.Application.Setup;
using Tausi.Domain.Setup;

namespace Tausi.Api.Endpoints;

public sealed record UnlockSetupRequest(string Token);

public sealed record MarkStepRequest(string? Summary);

public sealed record ServerAddress(string Address, string Interface);

public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this WebApplication app)
    {
        var setup = app.MapGroup("/api/setup").WithTags("Setup");

        setup.MapGet("/status", (HttpContext http, SetupFlow flow, CancellationToken ct) => flow.Status(http.Request.Cookies[SetupMode.Cookie], ct));

        // A lost admin password: the token `TausiServer reset-admin` printed on the server.
        setup.MapPost("/recover", async (RecoverAdminRequest r, AdminRecovery recovery, CancellationToken ct) =>
            new { userName = await recovery.Recover(r, ct) });

        setup.MapPost("/unlock", async (UnlockSetupRequest r, HttpContext http, SetupFlow flow, CancellationToken ct) =>
        {
            var session = await flow.Unlock(r.Token, ct);
            http.Response.Cookies.Append(SetupMode.Cookie, session, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps, Path = "/", MaxAge = TimeSpan.FromDays(7),
            });
            return await flow.Status(session, ct);
        });

        // Everything below needs the browser that unlocked the wizard.
        var wizard = setup.MapGroup("").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            await http.RequestServices.GetRequiredService<SetupFlow>().RequireSession(http.Request.Cookies[SetupMode.Cookie], http.RequestAborted);
            return await next(context);
        });

        wizard.MapPut("/business", async (SetupBusinessRequest r, SetupBusiness business, HttpContext http, SetupFlow flow, CancellationToken ct) =>
        {
            await business.Save(r, ct);
            return await flow.Status(http.Request.Cookies[SetupMode.Cookie], ct);
        });
        wizard.MapPost("/logo", async (IFormFile file, LogoCommands logo, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return await logo.Store(new LogoUpload(file.FileName, file.ContentType, file.Length, stream), null, SetupFlow.SetupActor, ct);
        }).DisableAntiforgery();

        // Step 2 signs the owner in, so the later steps use the ordinary admin pages' API as them.
        wizard.MapPost("/admin", async (SetupAdminRequest r, SetupPeople people, HttpContext http, SetupFlow flow, CancellationToken ct) =>
        {
            var user = await people.Admin(r, ct);
            var identity = new ClaimsIdentity(
                [new Claim(TausiClaims.UserId, user.UserId.ToString()), new Claim(TausiClaims.Name, user.Name)],
                TausiClaims.AdminScheme, TausiClaims.Name, null);
            await http.SignInAsync(TausiClaims.AdminScheme, new ClaimsPrincipal(identity));
            return await flow.Status(http.Request.Cookies[SetupMode.Cookie], ct);
        });
        wizard.MapPost("/staff", (SetupStaffRequest r, SetupPeople people, CancellationToken ct) => people.Staff(r, ct))
            .RedactOnReplay("people", "pinSheetPdf");

        wizard.MapPost("/steps/{step}/done", async (string step, MarkStepRequest r, SetupFlow flow, HttpContext http, CancellationToken ct) =>
        {
            await flow.Mark(step, SetupStepStatus.Done, r.Summary, ct);
            return await flow.Status(http.Request.Cookies[SetupMode.Cookie], ct);
        });
        wizard.MapPost("/steps/{step}/skip", async (string step, SetupFlow flow, HttpContext http, CancellationToken ct) =>
        {
            await flow.Mark(step, SetupStepStatus.Skipped, null, ct);
            return await flow.Status(http.Request.Cookies[SetupMode.Cookie], ct);
        });

        wizard.MapGet("/addresses", (HttpContext http) => Addresses(http));

        wizard.MapPost("/licence", async (EnterLicenceRequest r, LicenceService licences, SetupFlow flow, CancellationToken ct) =>
        {
            var status = await licences.Enter(r, asSetup: true, ct);
            await flow.Mark(SetupSteps.Licence, SetupStepStatus.Done, $"{status.Edition} licence, {status.TerminalLimit} terminals", ct);
            return status;
        });
        wizard.MapPost("/licence/trial", async (LicenceService licences, SetupFlow flow, CancellationToken ct) =>
        {
            var status = await licences.StartTrial(asSetup: true, ct);
            await flow.Mark(SetupSteps.Licence, SetupStepStatus.Done, $"30-day trial, {status.TerminalLimit} terminals", ct);
            return status;
        });
        wizard.MapGet("/summary", (SetupSummary summary, CancellationToken ct) => summary.Build(ct));

        wizard.MapPost("/finish", async (SetupFlow flow, HttpContext http, CancellationToken ct) =>
        {
            await flow.Finish(ct);
            http.Response.Cookies.Delete(SetupMode.Cookie);
            return Results.NoContent();
        });
    }

    /// <summary>This server's LAN addresses with its port, for the terminals: "http://192.168.1.10:5080".</summary>
    public static IReadOnlyList<ServerAddress> Addresses(HttpContext http)
    {
        var port = http.Connection.LocalPort > 0 ? http.Connection.LocalPort : 5080;
        var list = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => new ServerAddress($"http://{a.Address}:{port}", n.Name)))
            .ToList();
        return list.Count > 0 ? list : [new ServerAddress($"http://{http.Request.Host}", "this address")];
    }
}
