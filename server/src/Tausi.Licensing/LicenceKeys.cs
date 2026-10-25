using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Tausi.Licensing;

/// <summary>What a licence key says. Dates are UTC; <see cref="Expires"/> null means it never expires.</summary>
public sealed record LicencePayload(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("business")] string Business,
    [property: JsonPropertyName("edition")] string Edition,
    [property: JsonPropertyName("maxTerminals")] int MaxTerminals,
    [property: JsonPropertyName("issued")] DateTimeOffset Issued,
    [property: JsonPropertyName("expires")] DateTimeOffset? Expires,
    [property: JsonPropertyName("supportUntil")] DateTimeOffset? SupportUntil);

/// <summary>Why a key was refused.</summary>
public enum LicenceKeyProblem
{
    None,
    Malformed,
    BadSignature,
}

/// <summary>
/// Licence keys: a JSON payload signed with Ed25519 and written as one copy-paste key,
/// "TAUSI-" then base32 of [payload length (2 bytes)][payload][signature (64 bytes)] in groups of
/// five. Checked offline with a public key; only NexaGen's licence tool holds the private key.
/// </summary>
public static class LicenceKeys
{
    public const string Prefix = "TAUSI-";
    private const int SignatureLength = 64;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Issue(LicencePayload payload, byte[] privateKey)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json));
        if (body.Length > ushort.MaxValue) throw new ArgumentException("The licence is too long.");
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey, 0));
        signer.BlockUpdate(body, 0, body.Length);
        var signature = signer.GenerateSignature();
        byte[] bytes = [(byte)(body.Length >> 8), (byte)body.Length, .. body, .. signature];
        return Prefix + Group(Base32.Encode(bytes));
    }

    /// <summary>Reads and checks a key against any of the trusted public keys.</summary>
    public static LicenceKeyProblem TryRead(string key, IEnumerable<byte[]> publicKeys, out LicencePayload? payload)
    {
        payload = null;
        var text = new string((key ?? "").Trim().ToUpperInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return LicenceKeyProblem.Malformed;
        var bytes = Base32.Decode(text[Prefix.Length..].Replace("-", ""));
        if (bytes is null || bytes.Length < 2 + SignatureLength) return LicenceKeyProblem.Malformed;
        var length = (bytes[0] << 8) | bytes[1];
        if (bytes.Length != 2 + length + SignatureLength) return LicenceKeyProblem.Malformed;
        var body = bytes.AsSpan(2, length).ToArray();
        var signature = bytes.AsSpan(2 + length, SignatureLength).ToArray();

        var trusted = publicKeys.Any(publicKey =>
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(body, 0, body.Length);
            return verifier.VerifySignature(signature);
        });
        if (!trusted) return LicenceKeyProblem.BadSignature;
        try
        {
            payload = JsonSerializer.Deserialize<LicencePayload>(body, Json);
        }
        catch (JsonException)
        {
            return LicenceKeyProblem.Malformed;
        }
        return payload is null ? LicenceKeyProblem.Malformed : LicenceKeyProblem.None;
    }

    /// <summary>A new key pair: (private key, public key), 32 bytes each.</summary>
    public static (byte[] PrivateKey, byte[] PublicKey) NewKeyPair()
    {
        var privateKey = new Ed25519PrivateKeyParameters(new SecureRandom());
        return (privateKey.GetEncoded(), privateKey.GeneratePublicKey().GetEncoded());
    }

    /// <summary>The business names match ignoring case and spaces ("Mau's Village" = "MAU'S  village").</summary>
    public static bool SameBusiness(string a, string b) => Squash(a) == Squash(b);

    private static string Squash(string s) => new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    private static string Group(string text) =>
        string.Join("-", Enumerable.Range(0, (text.Length + 4) / 5).Select(i => text.Substring(i * 5, Math.Min(5, text.Length - i * 5))));
}

/// <summary>RFC 4648 base32 without padding (letters and 2–7: easy to read aloud and type).</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[]? Decode(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) return null;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
