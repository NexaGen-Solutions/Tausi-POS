using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Tausi.Infrastructure.Security;

/// <summary>
/// Server-side secrets (the PIN pepper and the session signing key). Taken from configuration
/// when given, otherwise generated once and kept in &lt;data&gt;/secrets. Losing the PIN pepper
/// means every PIN must be reset, so the data directory is part of the backup. A restore brings
/// back the backup's secrets and calls <see cref="Reload"/>.
/// </summary>
public sealed class ServerSecrets
{
    private readonly TausiOptions _options;
    private readonly string _dir;
    private volatile Keys _keys;

    private sealed record Keys(byte[] PinPepper, byte[] JwtSigningKey);

    public ServerSecrets(IOptions<TausiOptions> options, IHostEnvironment env)
    {
        _options = options.Value;
        _dir = Path.Combine(Path.GetFullPath(_options.DataDirectory, env.ContentRootPath), "secrets");
        _keys = Load();
    }

    public byte[] PinPepper => _keys.PinPepper;
    public byte[] JwtSigningKey => _keys.JwtSigningKey;

    /// <summary>Reads the secrets again (after a restore replaced the data folder).</summary>
    public void Reload() => _keys = Load();

    private Keys Load() => new(
        FromConfigOrFile(_options.PinPepper, Path.Combine(_dir, "pin-pepper.key")),
        FromConfigOrFile(_options.JwtSigningKey, Path.Combine(_dir, "session-signing.key")));

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
