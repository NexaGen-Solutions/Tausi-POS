using System.Security.Cryptography;
using System.Text;
using Nexagen.Application.Security;

namespace Nexagen.Infrastructure.Security;

/// <summary>PBKDF2 (SHA-512) for PINs and passwords, plus an HMAC lookup key for PINs.</summary>
public sealed class PinHasher(ServerSecrets secrets) : IPinHasher
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA512, HashBytes);
        return $"pbkdf2-sha512${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string secret, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha512" || !int.TryParse(parts[1], out var iterations))
            return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public string Lookup(string pin) =>
        Convert.ToHexString(HMACSHA256.HashData(secrets.PinPepper, Encoding.UTF8.GetBytes("pin:" + pin)));
}
