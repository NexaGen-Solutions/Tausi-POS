using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Tausi.Relay;

/// <summary>NexaGen's own login for the admin page: one user, its password kept as a PBKDF2 hash in the settings.</summary>
public static class AdminPasswords
{
    private const int Iterations = 210_000;

    /// <summary>"pbkdf2:210000:&lt;salt&gt;:&lt;hash&gt;" (base64).</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>
/// The NexaGen-only admin page: sign in, see each business (last seen, payments waiting and in the
/// last 30 days), add one (its API key is shown once, with the addresses to register), rotate a key,
/// switch a business off or on. Plain HTML forms with antiforgery tokens.
/// </summary>
public static class Admin
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public static void MapAdmin(this WebApplication app)
    {
        app.MapGet("/admin/login", (HttpContext http, IAntiforgery af) => Page("Sign in", Login(http, af, null)));
        app.MapPost("/admin/login", async (HttpContext http, IOptions<RelayOptions> options, IAntiforgery af) =>
        {
            var form = await http.Request.ReadFormAsync();
            var o = options.Value;
            var ok = !string.IsNullOrEmpty(o.AdminPasswordHash) && form["user"] == o.AdminUser &&
                     AdminPasswords.Verify(form["password"].ToString(), o.AdminPasswordHash);
            if (!ok) return Page("Sign in", Login(http, af, "That user name and password don't match."));
            await http.SignInAsync(Scheme, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, o.AdminUser)], Scheme)));
            return Results.Redirect("/admin");
        });
        app.MapPost("/admin/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(Scheme);
            return Results.Redirect("/admin/login");
        }).RequireAuthorization();

        app.MapGet("/admin", async (HttpContext http, RelayDb db, IAntiforgery af, TimeProvider clock, CancellationToken ct) =>
            Page("Businesses", await List(http, db, af, clock, ct))).RequireAuthorization();

        app.MapPost("/admin/businesses", async (HttpContext http, RelayDb db, IAntiforgery af, IOptions<RelayOptions> options, TimeProvider clock,
            CancellationToken ct) =>
        {
            var name = (await http.Request.ReadFormAsync(ct))["name"].ToString().Trim();
            if (name.Length is 0 or > 200) return Page("Businesses", "<p class=error>Give the business a name.</p>" + await List(http, db, af, clock, ct));
            var key = RelaySecrets.NewApiKey();
            var business = new Business
            {
                Name = name, ApiKeyHash = RelaySecrets.Hash(key), ApiKeyPrefix = RelaySecrets.PrefixOf(key), PathToken = RelaySecrets.NewPathToken(),
                CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            db.Businesses.Add(business);
            await db.SaveChangesAsync(ct);
            return Page("Business added", Secret(business, key, options.Value.PublicUrl, "added"));
        }).RequireAuthorization();

        app.MapPost("/admin/businesses/{id:guid}/rotate", async (Guid id, RelayDb db, IOptions<RelayOptions> options, CancellationToken ct) =>
        {
            var business = await db.Businesses.FirstOrDefaultAsync(b => b.Id == id, ct);
            if (business is null) return Results.NotFound();
            var key = RelaySecrets.NewApiKey();
            business.ApiKeyHash = RelaySecrets.Hash(key);
            business.ApiKeyPrefix = RelaySecrets.PrefixOf(key);
            await db.SaveChangesAsync(ct);
            return Page("New API key", Secret(business, key, options.Value.PublicUrl, "given a new key; the old key stopped working"));
        }).RequireAuthorization();

        app.MapPost("/admin/businesses/{id:guid}/active", async (Guid id, RelayDb db, CancellationToken ct) =>
        {
            var business = await db.Businesses.FirstOrDefaultAsync(b => b.Id == id, ct);
            if (business is null) return Results.NotFound();
            business.Active = !business.Active;
            await db.SaveChangesAsync(ct);
            return Results.Redirect("/admin");
        }).RequireAuthorization();
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string Token(HttpContext http, IAntiforgery af)
    {
        var tokens = af.GetAndStoreTokens(http);
        return $"<input type=hidden name=\"{E(tokens.FormFieldName)}\" value=\"{E(tokens.RequestToken)}\">";
    }

    private static string Login(HttpContext http, IAntiforgery af, string? error) =>
        (error is null ? "" : $"<p class=error>{E(error)}</p>") +
        $"<form method=post action=/admin/login>{Token(http, af)}" +
        "<label>User name <input name=user autocomplete=username required></label>" +
        "<label>Password <input name=password type=password autocomplete=current-password required></label>" +
        "<button>Sign in</button></form>";

    private static async Task<string> List(HttpContext http, RelayDb db, IAntiforgery af, TimeProvider clock, CancellationToken ct)
    {
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        var rows = await db.Businesses.AsNoTracking().OrderBy(b => b.Name).Select(b => new
        {
            b.Id, b.Name, b.ApiKeyPrefix, b.LastSeenAt, b.Active, b.CreatedAt,
            Waiting = db.Payments.Count(p => p.BusinessId == b.Id && p.AcknowledgedAt == null),
            Month = db.Payments.Count(p => p.BusinessId == b.Id && p.ReceivedAt >= since),
        }).ToListAsync(ct);
        var token = Token(http, af);
        var html = new StringBuilder();
        html.Append("<table><thead><tr><th>Business</th><th>Key</th><th>Last seen (UTC)</th><th>Waiting</th><th>Last 30 days</th><th></th></tr></thead><tbody>");
        foreach (var r in rows)
            html.Append($"<tr{(r.Active ? "" : " class=off")}><td>{E(r.Name)}</td><td><code>{E(r.ApiKeyPrefix)}…</code></td>")
                .Append($"<td>{(r.LastSeenAt is { } seen ? seen.ToString("yyyy-MM-dd HH:mm") : "never")}</td><td>{r.Waiting}</td><td>{r.Month}</td><td>")
                .Append($"<form method=post action=/admin/businesses/{r.Id}/rotate>{token}<button>New key</button></form> ")
                .Append($"<form method=post action=/admin/businesses/{r.Id}/active>{token}<button>{(r.Active ? "Switch off" : "Switch on")}</button></form>")
                .Append("</td></tr>");
        html.Append("</tbody></table>");
        html.Append($"<h2>Add a business</h2><form method=post action=/admin/businesses>{token}<label>Name <input name=name maxlength=200 required></label><button>Add</button></form>");
        html.Append($"<form method=post action=/admin/logout>{token}<button>Sign out</button></form>");
        return html.ToString();
    }

    private static string Secret(Business business, string key, string publicUrl, string what)
    {
        var root = publicUrl.TrimEnd('/') + "/pay/" + business.PathToken;
        return $"<p>{E(business.Name)} was {E(what)}.</p>" +
               "<p><strong>Copy this API key now. It is shown only once.</strong> Type it into the bar's Settings → M-Pesa → Relay.</p>" +
               $"<pre>{E(key)}</pre>" +
               "<p>The bar's server registers these addresses with Daraja itself (Test connection):</p>" +
               $"<pre>{E(root)}/stk\n{E(root)}/c2b/validation\n{E(root)}/c2b/confirmation</pre>" +
               "<p><a href=/admin>Back to the list</a></p>";
    }

    private static IResult Page(string title, string body) =>
        Results.Content(
            "<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\">" +
            $"<title>{E(title)} · Tausi relay</title><style>" +
            "body{font-family:system-ui,sans-serif;margin:0;background:#F4F4F2;color:#1C1C1C}header{background:#0A4A52;color:#F6F1E4;padding:12px 16px}" +
            "main{padding:16px;max-width:1100px}table{border-collapse:collapse;width:100%;background:#fff}th,td{border-bottom:1px solid #C9C9C4;padding:8px;text-align:left}" +
            "form{display:inline-block;margin:4px 0}label{display:block;margin:8px 0}input{min-height:36px;padding:0 8px}" +
            "button{min-height:36px;background:#0E6670;color:#fff;border:0;border-radius:4px;padding:0 12px}pre{background:#fff;border:1px solid #C9C9C4;padding:8px;overflow:auto}" +
            ".error{color:#8A3708}.off td{color:#5C5C58}</style></head><body>" +
            $"<header>Tausi relay · NexaGen Solutions</header><main><h1>{E(title)}</h1>{body}</main></body></html>",
            "text/html; charset=utf-8");
}
