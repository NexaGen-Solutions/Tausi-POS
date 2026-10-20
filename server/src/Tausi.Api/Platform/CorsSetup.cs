namespace Tausi.Api;

/// <summary>
/// Before pairing, the terminal app shows its bundled setup screen (origin tauri://localhost or
/// http://tauri.localhost) and calls a few anonymous endpoints across origins. Everything else
/// is same-origin: once paired, the app shows pages served by this server.
/// </summary>
public static class CorsSetup
{
    public const string TerminalApp = "TerminalApp";

    public static IServiceCollection AddTausiCors(this IServiceCollection services) =>
        services.AddCors(o => o.AddPolicy(TerminalApp, p => p
            .WithOrigins("tauri://localhost", "http://tauri.localhost", "https://tauri.localhost")
            .AllowAnyHeader()
            .WithMethods("GET", "POST")));
}
