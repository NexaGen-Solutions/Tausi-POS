using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tausi.Application.Setup;

namespace Tausi.Infrastructure.Platform;

/// <summary>setup-token.txt (in the data folder, or an installed server's home folder): the setup token for whoever installs the server. Deleted when setup is finished.</summary>
public sealed class SetupTokenFile(IOptions<TausiOptions> options, IHostEnvironment env) : ISetupTokenFile
{
    public string Path { get; } = options.Value.SetupTokenFile is { Length: > 0 } file
        ? System.IO.Path.GetFullPath(file, env.ContentRootPath)
        : System.IO.Path.Combine(System.IO.Path.GetFullPath(options.Value.DataDirectory, env.ContentRootPath), "setup-token.txt");

    public string? Read() => File.Exists(Path) ? File.ReadAllText(Path).Trim() : null;

    public void Write(string token)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, token + Environment.NewLine);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Delete()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}
