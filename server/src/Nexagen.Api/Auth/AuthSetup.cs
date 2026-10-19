using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nexagen.Application.Auth;
using Nexagen.Application.Security;
using Nexagen.Infrastructure;

namespace Nexagen.Api.Auth;

public static class AuthSetup
{
    private const string SessionExpiredItem = "nexagen.session_expired";

    /// <summary>
    /// Two kinds of session, one policy:
    /// terminals send a PIN-login token (Bearer) that only works together with their device token
    /// and while its server-side session is alive; the admin site uses a cookie from the
    /// user-name + password sign-in.
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
                    OnTokenValidated = ValidatePinSession,
                    OnChallenge = async ctx =>
                    {
                        if (!ctx.HttpContext.Items.ContainsKey(SessionExpiredItem)) return;
                        ctx.HandleResponse();
                        await WriteSessionExpired(ctx.HttpContext);
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

        services.AddAuthorizationBuilder()
            .AddPolicy(NexagenClaims.DeviceOrSessionPolicy, p => p.RequireAssertion(ctx =>
                ctx.User.Identity?.IsAuthenticated == true
                || (ctx.Resource as HttpContext)?.Features.Get<DeviceTerminal>() is not null));
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentSession, HttpCurrentSession>();
        services.AddScoped<IApprovalTokenSource, HeaderApprovalTokenSource>();
        return services;
    }

    /// <summary>True when this request carried a PIN session that has ended.</summary>
    public static bool SessionExpired(HttpContext http) => http.Items.ContainsKey(SessionExpiredItem);

    /// <summary>
    /// A PIN token is valid only on the terminal it was issued to (checked against the device
    /// token), and only while its server-side session is alive. Each request refreshes the idle
    /// timer, except hub connections, which are not "use".
    /// </summary>
    private static async Task ValidatePinSession(TokenValidatedContext ctx)
    {
        var http = ctx.HttpContext;
        var terminal = http.Features.Get<DeviceTerminal>();
        var claims = ctx.Principal!;
        if (terminal is null || claims.FindFirst(NexagenClaims.TerminalId)?.Value != terminal.TerminalId.ToString())
        {
            ctx.Fail("This session belongs to another terminal.");
            return;
        }
        if (!Guid.TryParse(claims.FindFirst(NexagenClaims.SessionId)?.Value, out var sessionId)
            || !Guid.TryParse(claims.FindFirst(NexagenClaims.UserId)?.Value, out var userId))
        {
            http.Items[SessionExpiredItem] = true;
            ctx.Fail(NexagenClaims.SessionExpiredCode);
            return;
        }

        var keeper = http.RequestServices.GetRequiredService<SessionKeeper>();
        var refresh = !http.Request.Path.StartsWithSegments("/hubs");
        var ended = await keeper.Check(sessionId, userId, terminal.TerminalId, refresh, http.RequestAborted);
        if (ended is not null)
        {
            http.Items[SessionExpiredItem] = true;
            ctx.Fail(NexagenClaims.SessionExpiredCode);
        }
    }

    public static async Task WriteSessionExpired(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        var problem = new ProblemDetails
        {
            Status = 401,
            Title = "Session expired",
            Detail = "You were logged out. Enter your PIN again.",
            Extensions = { ["code"] = NexagenClaims.SessionExpiredCode },
        };
        await http.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
    }

    private sealed class ConfigurePinScheme(SessionTokens tokens, TimeProvider clock) : IPostConfigureOptions<JwtBearerOptions>
    {
        public void PostConfigure(string? name, JwtBearerOptions o)
        {
            if (name != NexagenClaims.PinScheme) return;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = SessionTokens.Issuer,
                ValidAudience = SessionTokens.Issuer,
                IssuerSigningKey = tokens.Key,
                NameClaimType = NexagenClaims.Name,
                // Use the server's clock (a fake one in tests), not DateTime.UtcNow.
                LifetimeValidator = (notBefore, expires, _, _) =>
                {
                    var now = clock.GetUtcNow().UtcDateTime;
                    return (notBefore is null || notBefore <= now.AddSeconds(30)) && expires is not null && now < expires;
                },
            };
        }
    }
}
