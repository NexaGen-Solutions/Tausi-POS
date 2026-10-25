using Tausi.Application.Auth;
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

        // The terminal app's setup screen asks for an admin-site login before the server address can change.
        terminals.MapPost("/verify-admin", async (AdminLoginRequest request, AdminLogin login, HttpContext http, CancellationToken ct) =>
        {
            await login.Execute(request, ct, http.Connection.RemoteIpAddress?.ToString());
            return Results.NoContent();
        }).RequireCors(CorsSetup.TerminalApp);

        // Leaving kiosk mode on a paired terminal: an admin-site login, recorded against the terminal.
        terminals.MapPost("/kiosk-exit", async (AdminLoginRequest request, KioskExit exit, HttpContext http, CancellationToken ct) =>
        {
            await exit.Execute(request, http.Connection.RemoteIpAddress?.ToString(), ct);
            return Results.NoContent();
        }).RequireCors(CorsSetup.TerminalApp);

        // The paired terminal, from its device token.
        app.MapGet("/api/device", (GetDeviceInfo info, CancellationToken ct) => info.Execute(ct)).WithTags("Terminals");

        // Admin (devices.manage).
        var admin = app.MapGroup("/api/admin/terminals").WithTags("Terminals").RequireAuthorization();
        admin.MapGet("/", (ListTerminals list, CancellationToken ct) => list.Execute(ct));
        admin.MapPut("/{id:guid}", (Guid id, TerminalRequest request, UpdateTerminal update, CancellationToken ct) =>
            update.Execute(id, request, ct));
        admin.MapPost("/pair", (PairTerminalRequest request, PairTerminal pair, CancellationToken ct) => pair.Execute(request, ct));
        admin.MapPost("/{id:guid}/revoke", async (Guid id, RevokeTerminal revoke, CancellationToken ct) =>
        {
            await revoke.Execute(id, ct);
            return Results.NoContent();
        });
        admin.MapPut("/{id:guid}/price-list", async (Guid id, TerminalPriceListRequest r, SetTerminalPriceList set, CancellationToken ct) =>
        {
            await set.Execute(id, r, ct);
            return Results.NoContent();
        });
    }
}
