using Tausi.Application.Terminals;

namespace Tausi.Api.Endpoints;

public static class TerminalEndpoints
{
    public static void MapTerminalEndpoints(this WebApplication app)
    {
        var terminals = app.MapGroup("/api/terminals").WithTags("Terminals");

        // Called by a terminal app that is not paired yet, so no login.
        terminals.MapPost("/pairing", (StartPairing start, CancellationToken ct) => start.Execute(ct))
            .RequireCors(CorsSetup.TerminalApp);
        terminals.MapPost("/pairing/claim", (ClaimPairingRequest request, ClaimPairing claim, CancellationToken ct) =>
            claim.Execute(request, ct)).RequireCors(CorsSetup.TerminalApp);

        // An admin enters the code shown on the terminal (devices.manage). The admin screen comes later.
        terminals.MapPost("/pair", (CompletePairingRequest request, CompletePairing pair, CancellationToken ct) =>
            pair.Execute(request, ct)).RequireAuthorization();

        // The paired terminal, from its device token.
        app.MapGet("/api/device", (GetDeviceInfo info, CancellationToken ct) => info.Execute(ct)).WithTags("Terminals");
    }
}
