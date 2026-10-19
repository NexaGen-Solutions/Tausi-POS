using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nexagen.Application.Security;
using Nexagen.Infrastructure.Security;

namespace Nexagen.Api.Auth;

/// <summary>Issues the PIN-login session: a signed token tied to one terminal, valid for a shift.</summary>
public sealed class SessionTokens(ServerSecrets secrets, TimeProvider clock)
{
    public const string Issuer = "nexagen";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public SymmetricSecurityKey Key { get; } = new(secrets.JwtSigningKey);

    public (string Token, DateTimeOffset ExpiresAt) Issue(SessionUser user)
    {
        var now = clock.GetUtcNow();
        var expires = now + Lifetime;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Issuer,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(NexagenClaims.UserId, user.UserId.ToString()),
                new Claim(NexagenClaims.Name, user.Name),
                new Claim(NexagenClaims.TerminalId, user.TerminalId!.Value.ToString()),
            ]),
        });
        return (token, expires);
    }
}
