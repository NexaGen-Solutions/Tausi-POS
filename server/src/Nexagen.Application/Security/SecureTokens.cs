using System.Security.Cryptography;
using System.Text;

namespace Nexagen.Application.Security;

/// <summary>Random tokens (device, approval, pairing) and the hashes we store instead of them.</summary>
public static class SecureTokens
{
    public static string New(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>SHA-256 is enough here: the tokens are long and random, unlike PINs.</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string Digits(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(_ => RandomNumberGenerator.GetInt32(10)));
}
