using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tausi.Application.Security;
using Tausi.Infrastructure.Security;

namespace Tausi.Api.Auth;

/// <summary>
/// Issues the PIN-login token: signed, tied to one terminal and one server-side session. The
/// token itself lasts at most 12 hours; the session behind it ends sooner when idle or logged out.
/// </summary>
public sealed class SessionTokens(ServerSecrets secrets, TimeProvider clock)
{
    public const string Issuer = "nexagen";

    public SymmetricSecurityKey Key { get; } = new(secrets.JwtSigningKey);

    public string Issue(SessionUser user, DateTimeOffset expiresAt)
    {
        var now = clock.GetUtcNow();
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Issuer,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(TausiClaims.UserId, user.UserId.ToString()),
                new Claim(TausiClaims.Name, user.Name),
                new Claim(TausiClaims.TerminalId, user.TerminalId!.Value.ToString()),
                new Claim(TausiClaims.SessionId, user.SessionId!.Value.ToString()),
            ]),
        });
    }
}
