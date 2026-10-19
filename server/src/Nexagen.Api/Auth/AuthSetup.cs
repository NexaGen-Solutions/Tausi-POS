using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nexagen.Application.Security;
using Nexagen.Infrastructure;

namespace Nexagen.Api.Auth;

public static class AuthSetup
{
    /// <summary>
    /// Two kinds of session, one policy:
    /// terminals send a PIN-login token (Bearer) that only works together with their device token;
    /// the admin site uses a cookie from the user-name + password sign-in.
    /// </summary>
    public static IServiceCollection AddNexagenAuth(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var dataDir = Path.GetFullPath(
            config.GetSection(NexagenOptions.Section).Get<NexagenOptions>()?.DataDirectory ?? "data", env.ContentRootPath);
        services.AddDataProtection()
            .SetApplicationName("Nexagen")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

        services.AddSingleton<SessionTokens>();
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, ConfigurePinScheme>();

        services.AddAuthentication(NexagenClaims.SessionScheme)
            .AddPolicyScheme(NexagenClaims.SessionScheme, "PIN or admin session", o =>
            {
                o.ForwardDefaultSelector = http =>
                    http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)
                    || (http.Request.Path.StartsWithSegments("/hubs") && http.Request.Query.ContainsKey("access_token"))
                        ? NexagenClaims.PinScheme
                        : NexagenClaims.AdminScheme;
            })
            .AddJwtBearer(NexagenClaims.PinScheme, o =>
            {
                o.MapInboundClaims = false;
                o.Events = new JwtBearerEvents
                {
                    // Browsers cannot set headers on WebSockets, so the hub takes the token in the query.
                    OnMessageReceived = ctx =>
                    {
                        if (ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                            ctx.Token ??= ctx.Request.Query["access_token"];
                        return Task.CompletedTask;
                    },
                    // A PIN session is only valid on the terminal it was issued to.
                    OnTokenValidated = ctx =>
                    {
                        var terminal = ctx.HttpContext.Features.Get<DeviceTerminal>();
                        var claimed = ctx.Principal?.FindFirst(NexagenClaims.TerminalId)?.Value;
                        if (terminal is null || claimed != terminal.TerminalId.ToString())
                            ctx.Fail("This session belongs to another terminal.");
                        return Task.CompletedTask;
                    },
                };
            })
            .AddCookie(NexagenClaims.AdminScheme, o =>
            {
                o.Cookie.Name = "nexagen_admin";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
                // An API answers 401/403; it never redirects to a login page.
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorization();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentSession, HttpCurrentSession>();
        services.AddScoped<IApprovalTokenSource, HeaderApprovalTokenSource>();
        return services;
    }

    private sealed class ConfigurePinScheme(SessionTokens tokens) : IPostConfigureOptions<JwtBearerOptions>
    {
        public void PostConfigure(string? name, JwtBearerOptions o)
        {
            if (name != NexagenClaims.PinScheme) return;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = SessionTokens.Issuer,
                ValidAudience = SessionTokens.Issuer,
                IssuerSigningKey = tokens.Key,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = NexagenClaims.Name,
            };
        }
    }
}
