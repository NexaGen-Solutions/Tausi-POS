using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Tausi.Application.Updates;
using Tausi.Domain.Common;
using Tausi.Infrastructure;

namespace Tausi.Api.Platform;

/// <summary>
/// Installing a server update. The service account may not install software, so on Windows the
/// checked installer is left in &lt;data&gt;\updates with pending.json, and the "Tausi POS Server Update"
/// scheduled task (made by the installer, run as SYSTEM, startable by the service) runs
/// <c>TausiServer apply-update</c>, which checks the signature again and runs the installer silently.
/// Linux servers update with the new package instead.
/// </summary>
public sealed class UpdateInstaller(IOptions<TausiOptions> options, IHostEnvironment env) : IUpdateInstaller
{
    public const string TaskName = "Tausi POS Server Update";
    public const string PendingFile = "pending.json";

    public sealed record Pending(string File, string Version, string Sha256, string Signature);

    public string Platform => OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";

    public string DownloadFolder => Folder(options.Value, env);

    public static string Folder(TausiOptions options, IHostEnvironment env) =>
        Path.Combine(Path.GetFullPath(options.DataDirectory, env.ContentRootPath), "updates");

    public string? CantInstall()
    {
        if (!OperatingSystem.IsWindows())
            return "On Linux, update with the new package: run its install.sh (see INSTALL.md).";
        if (!WindowsServiceHelpers.IsWindowsService())
            return "Updates install themselves only on an installed Tausi POS Server (the Windows service).";
        return null;
    }

    public void Launch(string installer, string version, string sha256, string signature)
    {
        File.WriteAllText(Path.Combine(DownloadFolder, PendingFile),
            JsonSerializer.Serialize(new Pending(Path.GetFileName(installer), version, sha256, signature)));
        using var process = Process.Start(new ProcessStartInfo("schtasks.exe", ["/Run", "/TN", TaskName])
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        }) ?? throw new DomainException("Couldn't start the update task.", UpdateCodes.CantInstall);
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
            throw new DomainException(
                $"Couldn't start the \"{TaskName}\" task: {process.StandardError.ReadToEnd().Trim()} Run the new installer on the server instead.",
                UpdateCodes.CantInstall);
    }
}
