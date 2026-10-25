using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Tausi.Licensing;

/// <summary>
/// Signs server updates: an Ed25519 signature over "tausi-update", the version and the installer's
/// SHA-256. A server applies an update only when the signature checks against the update public key
/// built into it. The private key is NexaGen's (a release secret), separate from the licence key.
/// </summary>
public static class UpdateSignatures
{
    public static byte[] Message(string version, string sha256Hex) =>
        Encoding.UTF8.GetBytes($"tausi-update\n{version.Trim()}\n{sha256Hex.Trim().ToLowerInvariant()}");

    public static string Sign(byte[] privateKey, string version, string sha256Hex)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey, 0));
        var message = Message(version, sha256Hex);
        signer.BlockUpdate(message, 0, message.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    public static bool Verify(byte[] publicKey, string version, string sha256Hex, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        byte[] sig;
        try
        {
            sig = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return false;
        }
        if (sig.Length != 64 || publicKey.Length != 32) return false;
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        var message = Message(version, sha256Hex);
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(sig);
    }
}
