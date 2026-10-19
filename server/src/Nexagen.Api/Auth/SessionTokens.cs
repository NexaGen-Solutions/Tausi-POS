using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nexagen.Application.Security;
using Nexagen.Infrastructure.Security;

namespace Nexagen.Api.Auth;

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
                new Claim(NexagenClaims.UserId, user.UserId.ToString()),
                new Claim(NexagenClaims.Name, user.Name),
                new Claim(NexagenClaims.TerminalId, user.TerminalId!.Value.ToString()),
                new Claim(NexagenClaims.SessionId, user.SessionId!.Value.ToString()),
            ]),
        });
    }
}
