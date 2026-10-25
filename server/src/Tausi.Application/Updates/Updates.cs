using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Application.Setup;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Licensing;

namespace Tausi.Application.Updates;

/// <summary>Version numbers MAJOR.MINOR.PATCH (anything after a '-' or '+' is ignored).</summary>
public static class SemVer
{
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim().TrimStart('v').Split('-', '+')[0];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 3 || parts.Any(p => !int.TryParse(p, out _))) return null;
        while (parts.Length < 3) parts = [.. parts, "0"];
        return new Version(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
    }

    /// <summary>Negative when a is older than b. Unreadable versions count as the oldest.</summary>
    public static int Compare(string? a, string? b) =>
        (Parse(a) ?? new Version(0, 0, 0)).CompareTo(Parse(b) ?? new Version(0, 0, 0));
}

/// <summary>
/// The terminal app versions this server works with. A terminal older than <see cref="Minimum"/>
/// updates before anyone can log in on it. Raise it when the API changes in a way an older terminal
/// app can't handle (a contract test checks the previous release against this server).
/// </summary>
public static class TerminalVersions
{
    public const string Minimum = "0.1.0";

    public const string Header = "X-Tausi-Version";
    public const string ServerHeader = "X-Tausi-Server-Version";
    public const string UpdateRequiredCode = "terminal_update_required";

    public static bool TooOld(string? terminalVersion) => SemVer.Parse(terminalVersion) is not null && SemVer.Compare(terminalVersion, Minimum) < 0;
}

/// <summary>One downloadable server installer in the update feed.</summary>
public sealed record UpdateAsset(string Url, string Sha256, string? Signature, long? Size);

/// <summary>NexaGen's updates.json: the newest release, the terminal version it needs, and an installer per platform.</summary>
public sealed record UpdateFeed(
    string Version,
    string? MinimumTerminalVersion,
    DateTimeOffset? PublishedAt,
    string? Notes,
    IReadOnlyDictionary<string, UpdateAsset> Server);

public sealed record UpdateCheck(
    string Current,
    string? Latest,
    bool Available,
    string? Notes,
    bool CanInstallHere,
    string? CantInstallReason,
    bool DayOpen,
    string? Error);

public sealed record ApplyUpdateRequest(bool Force, string? Confirm);

/// <summary>update.json inside a .tausiupdate package (an update brought on a USB drive).</summary>
public sealed record UpdatePackageInfo(string Version, string Platform, string File, string Sha256, string? Signature, string? Notes);

/// <summary>Fetches the feed and downloads installers (HTTPS).</summary>
public interface IUpdateSource
{
    Task<UpdateFeed> Fetch(CancellationToken ct);
    Task Download(string url, string path, CancellationToken ct);
}

/// <summary>Runs a server installer, which stops this server, backs up, upgrades and starts it again.</summary>
public interface IUpdateInstaller
{
    /// <summary>"windows-x64" or "linux-x64".</summary>
    string Platform { get; }

    /// <summary>Null when this server can install an update itself; else why not.</summary>
    string? CantInstall();

    /// <summary>Where downloads go (not part of backups).</summary>
    string DownloadFolder { get; }

    /// <summary>Hands the checked installer over to be run (on Windows, by the SYSTEM update task).</summary>
    void Launch(string installer, string version, string sha256, string signature);
}

public static class UpdateCodes
{
    public const string DayOpen = "update_day_open";
    public const string BadSignature = "update_signature";
    public const string NoUpdate = "update_none";
    public const string CantInstall = "update_cant_install";
}

/// <summary>
/// Settings → System: Check for updates (NexaGen's feed, when the server has internet), Apply (only with
/// the day closed, or forced by typing UPDATE), and an update package uploaded from a USB drive. Every
/// installer is checked against its SHA-256 and the update signature before it runs. Needs <c>settings.manage</c>.
/// </summary>
public sealed class ServerUpdates(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IUpdateSource source, IUpdateInstaller installer,
    IAuditLog audit, LicenceOptions licenceOptions)
{
    public const string ProductionPublicKey = "FSbGWaIs2pJwkErjVDkRreHtbBapHQXQDkLf3kH8T1s=";

    /// <summary>The tests' update key, accepted only where the tests' licence key is (Development).</summary>
    public const string TestPublicKey = "ansU2LrS4Gks32W5jfv80voCXLJbUkidRQ0Plqna1e4=";

    public const string ConfirmWord = "UPDATE";

    public static string Current => typeof(ServerUpdates).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private async Task Require(CancellationToken ct) =>
        await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, null, ct);

    public sealed record SystemInfo(string Version, string Platform, string MinimumTerminalVersion, string? CantInstallReason);

    public async Task<SystemInfo> Info(CancellationToken ct)
    {
        await Require(ct);
        return new SystemInfo(Current, installer.Platform, TerminalVersions.Minimum, installer.CantInstall());
    }

    public async Task<UpdateCheck> Check(CancellationToken ct)
    {
        await Require(ct);
        var dayOpen = await db.WorkPeriods.AnyAsync(w => w.IsOpen, ct);
        var cant = installer.CantInstall();
        try
        {
            var feed = await source.Fetch(ct);
            var available = SemVer.Compare(feed.Version, Current) > 0 && feed.Server.ContainsKey(installer.Platform);
            return new UpdateCheck(Current, feed.Version, available, feed.Notes, cant is null, cant, dayOpen, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // No internet is normal at a bar: say so, and offer the USB package instead.
            return new UpdateCheck(Current, null, false, null, cant is null, cant, dayOpen,
                "Couldn't reach the update server. Check the internet connection, or upload an update package from a USB drive.");
        }
    }

    public async Task Apply(ApplyUpdateRequest request, CancellationToken ct)
    {
        await Require(ct);
        await CanStart(request, ct);
        var feed = await source.Fetch(ct);
        if (SemVer.Compare(feed.Version, Current) <= 0 || !feed.Server.TryGetValue(installer.Platform, out var asset))
            throw new DomainException("This server is up to date.", UpdateCodes.NoUpdate);

        Directory.CreateDirectory(installer.DownloadFolder);
        var file = Path.Combine(installer.DownloadFolder, SafeName(Path.GetFileName(new Uri(asset.Url).LocalPath), feed.Version));
        await source.Download(asset.Url, file, ct);
        await Start(file, feed.Version, asset.Sha256, asset.Signature, request, ct);
    }

    /// <summary>A .tausiupdate from a USB drive: update.json and the installer it names, checked the same way.</summary>
    public async Task Upload(Stream package, ApplyUpdateRequest request, CancellationToken ct)
    {
        await Require(ct);
        await CanStart(request, ct);
        Directory.CreateDirectory(installer.DownloadFolder);
        var zipPath = Path.Combine(installer.DownloadFolder, Guid.NewGuid().ToString("N") + ".tausiupdate");
        await using (var f = new FileStream(zipPath, FileMode.CreateNew))
            await package.CopyToAsync(f, ct);
        try
        {
            UpdatePackageInfo info;
            string file;
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.GetEntry("update.json") ?? throw new InvalidDataException("no update.json");
                await using (var s = entry.Open())
                    info = await JsonSerializer.DeserializeAsync<UpdatePackageInfo>(s, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct)
                           ?? throw new InvalidDataException("empty update.json");
                if (info.Platform != installer.Platform)
                    throw new DomainException($"This package is for {info.Platform}; this server is {installer.Platform}.", UpdateCodes.CantInstall);
                var installerEntry = zip.GetEntry(info.File) ?? throw new InvalidDataException($"no {info.File}");
                file = Path.Combine(installer.DownloadFolder, SafeName(info.File, info.Version));
                installerEntry.ExtractToFile(file, overwrite: true);
            }
            catch (InvalidDataException e)
            {
                throw new DomainException($"That isn't a Tausi POS update package ({e.Message}).", UpdateCodes.NoUpdate);
            }
            if (SemVer.Compare(info.Version, Current) <= 0)
                throw new DomainException($"This package is version {info.Version}; the server already has {Current}.", UpdateCodes.NoUpdate);
            await Start(file, info.Version, info.Sha256, info.Signature, request, ct);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    /// <summary>Updates stop the server: only with the day closed, or forced by typing UPDATE.</summary>
    private async Task CanStart(ApplyUpdateRequest request, CancellationToken ct)
    {
        if (installer.CantInstall() is { } why) throw new DomainException(why, UpdateCodes.CantInstall);
        if (await db.WorkPeriods.AnyAsync(w => w.IsOpen, ct)
            && !(request.Force && string.Equals(request.Confirm?.Trim(), ConfirmWord, StringComparison.Ordinal)))
            throw new DomainException(
                $"The day is open. Updating stops the POS for a few minutes: close the day first, or type {ConfirmWord} to update now.",
                UpdateCodes.DayOpen);
    }

    private async Task Start(string file, string version, string sha256, string? signature, ApplyUpdateRequest request, CancellationToken ct)
    {
        if (!await Verify(file, version, sha256, signature, licenceOptions.AcceptTestKey, ct))
        {
            File.Delete(file);
            throw new DomainException("The update's signature doesn't check out, so it wasn't installed.", UpdateCodes.BadSignature);
        }
        audit.Write(new AuditRecord(AuditActions.ServerUpdateStarted, "Server", version,
            OldValue: new { version = Current }, NewValue: new { version, forced = request.Force }));
        await db.SaveChangesAsync(ct);
        installer.Launch(file, version, sha256.Trim().ToLowerInvariant(), signature!);
    }

    /// <summary>The installer's SHA-256 matches, and NexaGen's update key signed that version and hash.</summary>
    public static async Task<bool> Verify(string file, string version, string sha256, string? signature, bool acceptTestKey, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        IEnumerable<string> keys = acceptTestKey ? [ProductionPublicKey, TestPublicKey] : [ProductionPublicKey];
        return keys.Any(k => UpdateSignatures.Verify(Convert.FromBase64String(k), version, actual, signature));
    }

    private static string SafeName(string name, string version)
    {
        var clean = string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
        return string.IsNullOrEmpty(clean) ? $"TausiPOS-Server-Setup-{version}.exe" : clean;
    }
}

/// <summary>The terminal app update this server offers (the installer puts it next to the server).</summary>
public sealed record TerminalUpdate(string Version, string? Notes, DateTimeOffset? PublishedAt, string File, string Signature);

public interface ITerminalUpdateFiles
{
    TerminalUpdate? Current();
    string? PathOf(string file);
}

/// <summary>
/// The terminals' update source: this server, not the internet, so bars without internet update too.
/// Server first: it never offers a terminal version newer than itself.
/// </summary>
public sealed class TerminalUpdateFeed(ITerminalUpdateFiles files)
{
    public TerminalUpdate? Offer(string serverVersion)
    {
        var update = files.Current();
        return update is not null && SemVer.Compare(update.Version, serverVersion) <= 0 ? update : null;
    }
}
