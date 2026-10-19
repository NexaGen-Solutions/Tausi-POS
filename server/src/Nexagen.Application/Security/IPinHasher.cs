namespace Nexagen.Application.Security;

public interface IPinHasher
{
    /// <summary>Slow salted hash (PBKDF2) for storing a PIN or password.</summary>
    string Hash(string secret);

    bool Verify(string secret, string hash);

    /// <summary>
    /// Deterministic keyed hash of a PIN (HMAC with a server secret), used to find the user and to
    /// keep PINs unique among active users.
    /// </summary>
    string Lookup(string pin);
}
