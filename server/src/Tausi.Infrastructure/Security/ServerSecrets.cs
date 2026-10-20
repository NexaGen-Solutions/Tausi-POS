using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Tausi.Infrastructure.Security;

/// <summary>
/// Server-side secrets (the PIN pepper and the session signing key). Taken from configuration
/// when given, otherwise generated once and kept in &lt;data&gt;/secrets. Losing the PIN pepper
/// means every PIN must be reset, so the data directory is part of the backup.
/// </summary>
public sealed class ServerSecrets
{
    public ServerSecrets(IOptions<TausiOptions> options, IHostEnvironment env)
    {
        var o = options.Value;
        var dir = Path.Combine(Path.GetFullPath(o.DataDirectory, env.ContentRootPath), "secrets");
        PinPepper = FromConfigOrFile(o.PinPepper, Path.Combine(dir, "pin-pepper.key"));
        JwtSigningKey = FromConfigOrFile(o.JwtSigningKey, Path.Combine(dir, "session-signing.key"));
    }

    public byte[] PinPepper { get; }
    public byte[] JwtSigningKey { get; }

    private static byte[] FromConfigOrFile(string? configured, string path)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Convert.FromBase64String(configured);
        if (File.Exists(path)) return Convert.FromBase64String(File.ReadAllText(path).Trim());

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }
}
