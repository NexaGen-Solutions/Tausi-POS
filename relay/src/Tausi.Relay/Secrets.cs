using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Tausi.Relay;

/// <summary>API keys and callback path tokens: random, URL-safe; keys are kept only as a SHA-256 hash.</summary>
public static class RelaySecrets
{
    public const string KeyPrefix = "trk_";

    public static string NewApiKey() => KeyPrefix + Random(32);

    public static string NewPathToken() => Random(24);

    public static string Hash(string apiKey) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

    /// <summary>"trk_Ab12Cd" — enough to tell keys apart, useless to an attacker.</summary>
    public static string PrefixOf(string apiKey) => apiKey.Length <= 10 ? apiKey : apiKey[..10];

    private static string Random(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Compares two hashes in constant time.</summary>
    public static bool SameHash(string a, string b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}

/// <summary>Phone numbers: only ever stored masked ("0712 ••• 678"). Safaricom's own C2B numbers may already be masked.</summary>
public static class Phones
{
    public static string? Mask(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        // Already masked by Safaricom ("2547 ***** 149"), or a hash: keep what can be shown safely.
        if (digits.Length < 9 || phone.Contains('*')) return phone.Length > 40 ? phone[..12] + "…" : phone.Trim();
        var local = digits.StartsWith("254") && digits.Length == 12 ? "0" + digits[3..] : digits;
        return local.Length >= 10 ? $"{local[..4]} ••• {local[^3..]}" : $"{local[..2]} ••• {local[^3..]}";
    }
}

/// <summary>An address range ("196.201.214.0/24" or a single address).</summary>
public sealed record AddressRange(IPAddress Network, int PrefixLength)
{
    public static AddressRange Parse(string text)
    {
        var parts = text.Trim().Split('/');
        var address = IPAddress.Parse(parts[0]);
        var bits = address.GetAddressBytes().Length * 8;
        return new AddressRange(address, parts.Length > 1 ? int.Parse(parts[1]) : bits);
    }

    public bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var a = Network.GetAddressBytes();
        var b = address.GetAddressBytes();
        if (a.Length != b.Length) return false;
        var left = PrefixLength;
        for (var i = 0; i < a.Length && left > 0; i++, left -= 8)
        {
            var mask = left >= 8 ? 0xFF : (byte)(0xFF << (8 - left));
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }
}
