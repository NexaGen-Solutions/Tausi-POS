using System.Security.Cryptography;
using System.Text;

namespace Tausi.Infrastructure.Platform;

/// <summary>
/// Secrets in the installed settings file (the database password, the backup password) are stored
/// as "dpapi:&lt;base64&gt;": encrypted with Windows DPAPI for this machine, so the file can be read by
/// anyone who can read it, but only decrypted on this PC. On Linux the settings file is readable by
/// the service user only, and secrets stay as given.
/// </summary>
public static class MachineSecrets
{
    public const string Prefix = "dpapi:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Tausi POS Server");

    public static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>Encrypts for this machine on Windows; elsewhere returns the value unchanged.</summary>
    public static string Protect(string value)
    {
        if (!OperatingSystem.IsWindows() || IsProtected(value)) return value;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string value)
    {
        if (!IsProtected(value)) return value;
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("A \"dpapi:\" secret can only be read on the Windows PC that wrote it.");
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                "A secret in the settings file can't be decrypted. It was written on another PC: enter it again with `TausiServer configure`.", e);
        }
    }

    /// <summary>A secret in a connection string: "Password=dpapi:..." becomes the plain password.</summary>
    public static string UnprotectConnectionString(string connection)
    {
        if (!connection.Contains(Prefix, StringComparison.Ordinal)) return connection;
        var parts = connection.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq > 0 && IsProtected(parts[i][(eq + 1)..].Trim()))
                parts[i] = parts[i][..(eq + 1)] + Unprotect(parts[i][(eq + 1)..].Trim());
        }
        return string.Join(';', parts);
    }
}
