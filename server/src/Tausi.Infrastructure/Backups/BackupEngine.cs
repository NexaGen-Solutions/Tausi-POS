using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tausi.Application.Backups;
using Tausi.Application.Common;
using Tausi.Domain.Common;
using Tausi.Application.Settings;
using Tausi.Application.Setup;
using Tausi.Infrastructure.Persistence;
using Tausi.Infrastructure.Platform;
using Tausi.Infrastructure.Security;

namespace Tausi.Infrastructure.Backups;

/// <summary>
/// Makes, checks, copies, keeps and restores the .tausibak backups in the backups folder. Its own
/// state (the list, the last result, the settings) lives in that folder, not the database: it has
/// to work when the database doesn't, and a restore must not bring back another PC's backup settings.
/// One backup or restore runs at a time.
/// </summary>
public sealed class BackupEngine(
    IServiceScopeFactory scopes,
    IOptions<TausiOptions> options,
    IOptions<DatabaseOptions> database,
    IConfiguration config,
    IHostEnvironment env,
    ServerSecrets secrets,
    MaintenanceGate gate,
    SetupGate setupGate,
    TimeProvider clock,
    ILogger<BackupEngine> log) : IBackupEngine
{
    public const int KeepNightly = 14;
    public const int KeepMonthly = 12;
    public static readonly TimeSpan KeepOthers = TimeSpan.FromDays(30);

    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static readonly string Version =
        typeof(BackupEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private TausiOptions O => options.Value;
    public string Folder => Path.GetFullPath(O.BackupDirectory, env.ContentRootPath);
    private string DataFolder => Path.GetFullPath(O.DataDirectory, env.ContentRootPath);
    private string WorkFolder => Path.Combine(Folder, "work");
    private string IndexPath => Path.Combine(Folder, "index.json");
    private string SettingsPath => Path.Combine(Folder, "backup-settings.json");

    // ---- State files ----

    private sealed record Entry(
        string Name, DateTimeOffset CreatedAt, BackupReason Reason, long Size, bool Verified, bool Encrypted,
        string? Business, string Version, string? CopiedTo, string? CopyError);

    private sealed record Index(
        DateTimeOffset FirstSeenAt,
        List<Entry> Backups,
        DateTimeOffset? LastSuccessAt = null,
        DateTimeOffset? LastFailureAt = null,
        string? LastFailure = null,
        BackupReason? LastFailureReason = null,
        DateOnly? LastNightlyDay = null);

    private sealed record StoredSettings(string Time = "05:00", string? CopyFolder = null, bool Encrypt = false, string? Password = null);

    private Index ReadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
                return JsonSerializer.Deserialize<Index>(File.ReadAllText(IndexPath), BackupFile.Json) ?? NewIndex();
        }
        catch (JsonException e)
        {
            log.LogError(e, "The backup list {Path} is unreadable; starting a new one", IndexPath);
        }
        return NewIndex();
    }

    private Index NewIndex() => new(clock.GetUtcNow(), []);

    private void WriteIndex(Index index) => WriteAtomically(IndexPath, JsonSerializer.Serialize(index, BackupFile.Json));

    private StoredSettings ReadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath), BackupFile.Json) ?? new();
        }
        catch (JsonException e)
        {
            log.LogError(e, "The backup settings {Path} are unreadable; using the defaults", SettingsPath);
        }
        return new();
    }

    private void WriteAtomically(string path, string text)
    {
        Directory.CreateDirectory(Folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    // ---- Settings ----

    public BackupSettingsDto Settings()
    {
        var s = ReadSettings();
        return new BackupSettingsDto(s.Time, s.CopyFolder, s.Encrypt, !string.IsNullOrEmpty(s.Password));
    }

    public void SaveSettings(SaveBackupSettingsRequest request)
    {
        var old = ReadSettings();
        var password = string.IsNullOrEmpty(request.Password) ? old.Password : MachineSecrets.Protect(request.Password);
        WriteAtomically(SettingsPath, JsonSerializer.Serialize(new StoredSettings(request.Time, request.CopyFolder, request.Encrypt, password), BackupFile.Json));
    }

    /// <summary>The time the nightly backup runs, in business time.</summary>
    public TimeOnly NightlyTime() => TimeOnly.TryParseExact(ReadSettings().Time, "HH:mm", out var t) ? t : new TimeOnly(5, 0);

    // ---- List and health ----

    public IReadOnlyList<BackupRecord> List() =>
        ReadIndex().Backups.Where(b => File.Exists(Path.Combine(Folder, b.Name))).OrderByDescending(b => b.CreatedAt)
            .Select(b => new BackupRecord(b.Name, b.CreatedAt, b.Reason, b.Size, b.Verified, b.Encrypted, b.Business, b.Version, b.CopiedTo, b.CopyError))
            .ToList();

    public string? PathOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || !name.EndsWith(BackupFile.Extension, StringComparison.Ordinal)) return null;
        var path = Path.Combine(Folder, name);
        return File.Exists(path) ? path : null;
    }

    public BackupHealth Health(DateTimeOffset now)
    {
        var index = ReadIndex();
        // A success clears the last failure, so a failure on record is the latest result.
        if (index.LastFailureAt is not null)
            return new BackupHealth("failed", index.LastSuccessAt, index.LastFailureReason == BackupReason.Nightly
                ? $"Last night's backup failed: {index.LastFailure}"
                : $"The last backup failed: {index.LastFailure}");
        var since = index.LastSuccessAt ?? index.FirstSeenAt;
        if (now - since > BackupHealth.Overdue)
            return new BackupHealth("overdue", index.LastSuccessAt, index.LastSuccessAt is null
                ? "There is no backup yet. Press Back up now in Settings → Backups."
                : $"No backup has worked for {(int)(now - since).TotalDays} days.");
        if (index.LastSuccessAt is null) return new BackupHealth("none", null, null);
        var last = index.Backups.LastOrDefault();
        if (last?.CopyError is not null)
            return new BackupHealth("copyFailed", index.LastSuccessAt, $"The backup copy failed: {last.CopyError} The backup on this PC is fine.");
        return new BackupHealth("ok", index.LastSuccessAt, null);
    }

    // ---- Backing up ----

    public async Task<BackupRecord> Run(BackupReason reason, CancellationToken ct)
    {
        if (gate.Message is not null && reason != BackupReason.BeforeRestore)
            throw new ConflictException("A restore is running. Try again when it's done.", "restore_running");
        await Lock.WaitAsync(ct);
        try
        {
            return await RunLocked(reason, ct);
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>The nightly backup, unless it already ran for this business day.</summary>
    public async Task RunNightly(DateOnly businessDay, CancellationToken ct)
    {
        await Lock.WaitAsync(ct);
        try
        {
            var index = ReadIndex();
            if (index.LastNightlyDay >= businessDay || gate.Message is not null) return;
            WriteIndex(index with { LastNightlyDay = businessDay });
            try
            {
                await RunLocked(BackupReason.Nightly, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // Already recorded as the last failure; the banner shows it.
                log.LogError(e, "The nightly backup failed");
            }
        }
        finally
        {
            Lock.Release();
        }
    }

    private async Task<BackupRecord> RunLocked(BackupReason reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var settings = ReadSettings();
        Directory.CreateDirectory(WorkFolder);
        var stamp = now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"tausi-{stamp}-{reason.ToString().ToLowerInvariant()}{BackupFile.Extension}";
        for (var n = 2; File.Exists(Path.Combine(Folder, name)); n++)
            name = $"tausi-{stamp}-{reason.ToString().ToLowerInvariant()}-{n}{BackupFile.Extension}";
        var final = Path.Combine(Folder, name);
        var dumpFile = Path.Combine(WorkFolder, $"{stamp}-{Guid.NewGuid():N}.db");
        var zipFile = Path.Combine(WorkFolder, $"{stamp}-{Guid.NewGuid():N}.zip");
        try
        {
            var (business, schema) = await DatabaseFacts(ct);
            var dump = Dump();
            await dump.Dump(dumpFile, ct);

            var files = new List<(string, string)> { (BackupFile.DatabaseFolder + dump.FileName, dumpFile) };
            files.AddRange(FilesOf(DataFolder, BackupFile.DataFolder, skip: ["updates", .. BackupsInsideData()]));
            if (O.ConfigDirectory is { } configFolder)
                files.AddRange(FilesOf(Path.GetFullPath(configFolder, env.ContentRootPath), BackupFile.ConfigFolder, skip: []));
            var manifest = new BackupManifest(BackupManifest.CurrentFormat, "Tausi POS", Version, schema, dump.Provider, business, now, reason, []);
            await BackupFile.Write(zipFile, manifest, files, ct);

            var encrypt = settings.Encrypt && !string.IsNullOrEmpty(settings.Password);
            var password = encrypt ? MachineSecrets.Unprotect(settings.Password!) : null;
            if (encrypt) await BackupFile.Encrypt(zipFile, final, password!, ct);
            else File.Move(zipFile, final);

            // Checked as it will be read: opened (decrypted with the password), every checksum compared.
            await Open(final, password, async zip => await BackupFile.Check(zip, ct), ct);

            var (copiedTo, copyError) = Copy(final, settings.CopyFolder);
            var entry = new Entry(name, now, reason, new FileInfo(final).Length, true, encrypt, business, Version, copiedTo, copyError);
            var index = ReadIndex();
            index.Backups.Add(entry);
            index = Retain(index with { LastSuccessAt = now, LastFailureAt = null, LastFailure = null, LastFailureReason = null });
            WriteIndex(index);
            if (copyError is not null) log.LogError("The backup {Name} worked, but its copy to {Folder} failed: {Error}", name, settings.CopyFolder, copyError);
            else log.LogInformation("Backup {Name} made and checked", name);
            return new BackupRecord(entry.Name, entry.CreatedAt, entry.Reason, entry.Size, entry.Verified, entry.Encrypted, entry.Business, entry.Version, entry.CopiedTo, entry.CopyError);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            TryDelete(final);
            var message = e is DomainException ? e.Message : e.GetBaseException().Message;
            WriteIndex(ReadIndex() with { LastFailureAt = now, LastFailure = message, LastFailureReason = reason });
            log.LogError(e, "Backup ({Reason}) failed: {Message}", reason, message);
            throw e as DomainException ?? new DomainException($"The backup failed: {message}", BackupCodes.Failed);
        }
        finally
        {
            TryDelete(dumpFile);
            TryDelete(zipFile);
        }
    }

    private IDatabaseDump Dump()
    {
        var connection = MachineSecrets.UnprotectConnectionString(config.GetConnectionString(TausiOptions.ConnectionStringName) ?? "");
        return database.Value.Provider == DatabaseProvider.SqlServer
            ? new SqlServerDump(connection, O.Backups, WorkFolder)
            : new PostgresDump(connection, O.Backups);
    }

    /// <summary>The business name and the last migration, for the manifest.</summary>
    private async Task<(string Business, string Schema)> DatabaseFacts(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TausiDbContext>();
        var settings = await SettingsRegistry.Load(db, ct);
        var schema = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "";
        return (settings.String(SettingsRegistry.BusinessName), schema);
    }

    /// <summary>The backups folder's name when it is inside the data folder (it is never backed up or replaced).</summary>
    private string[] BackupsInsideData()
    {
        var relative = Path.GetRelativePath(DataFolder, Folder).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? [] : [relative];
    }

    private static IEnumerable<(string Entry, string Source)> FilesOf(string folder, string prefix, string[] skip)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (skip.Any(s => relative == s || relative.StartsWith(s + "/", StringComparison.Ordinal))) continue;
            if (relative.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            yield return (prefix + relative, file);
        }
    }

    /// <summary>The second copy: never fails the backup, only records why it didn't work.</summary>
    private (string? CopiedTo, string? Error) Copy(string file, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return (null, null);
        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, Path.GetFileName(file));
            File.Copy(file, target + ".tmp", overwrite: true);
            File.Move(target + ".tmp", target, overwrite: true);
            return (folder, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, $"Couldn't copy to {folder}: {e.Message}");
        }
    }

    // ---- Keeping ----

    /// <summary>
    /// Keeps the last <see cref="KeepNightly"/> nightly backups, the first nightly backup of each of the last
    /// <see cref="KeepMonthly"/> months, and other backups for <see cref="KeepOthers"/>. Deletes the rest, here and in the copy folder.
    /// </summary>
    private Index Retain(Index index)
    {
        var keep = Keep(index.Backups.Select(b => (b.Name, b.CreatedAt, b.Reason)).ToList(), clock.GetUtcNow());
        var copyFolder = ReadSettings().CopyFolder;
        foreach (var gone in index.Backups.Where(b => !keep.Contains(b.Name)).ToList())
        {
            TryDelete(Path.Combine(Folder, gone.Name));
            if (gone.CopiedTo is not null) TryDelete(Path.Combine(gone.CopiedTo, gone.Name));
            else if (copyFolder is not null) TryDelete(Path.Combine(copyFolder, gone.Name));
            index.Backups.Remove(gone);
        }
        return index;
    }

    /// <summary>The names to keep (the retention rule on its own, for tests).</summary>
    public static HashSet<string> Keep(IReadOnlyList<(string Name, DateTimeOffset CreatedAt, BackupReason Reason)> backups, DateTimeOffset now)
    {
        var nightly = backups.Where(b => b.Reason == BackupReason.Nightly).OrderByDescending(b => b.CreatedAt).ToList();
        var keep = nightly.Take(KeepNightly).Select(b => b.Name).ToHashSet();
        var firstMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-(KeepMonthly - 1));
        foreach (var month in nightly.Where(b => b.CreatedAt >= firstMonth).GroupBy(b => (b.CreatedAt.Year, b.CreatedAt.Month)))
            keep.Add(month.MinBy(b => b.CreatedAt).Name);
        foreach (var other in backups.Where(b => b.Reason != BackupReason.Nightly && now - b.CreatedAt <= KeepOthers))
            keep.Add(other.Name);
        return keep;
    }

    // ---- Restoring ----

    public async Task<BackupContents> Inspect(string path, string? password, CancellationToken ct) =>
        await Open(path, password, async zip =>
        {
            using var archive = ZipFile.OpenRead(zip);
            var m = await BackupFile.ReadManifest(archive, ct);
            return new BackupContents(m.Business, m.CreatedAt, m.Version, m.Schema, m.Provider, BackupFile.IsEncrypted(path));
        }, ct);

    public async Task Restore(string path, string? password, CancellationToken ct, bool safetyBackup = true)
    {
        using var maintenance = gate.Begin(BackupAdmin.RestoringMessage);
        await Lock.WaitAsync(ct);
        try
        {
            await Open(path, password, async zip =>
            {
                var manifest = await BackupFile.Check(zip, ct);
                var dump = Dump();
                if (manifest.Provider != dump.Provider)
                    throw new DomainException(
                        $"This backup is of a {manifest.Provider} database and this server uses {dump.Provider}. Restore it on a server with the same database.",
                        BackupCodes.Invalid);

                // A safety backup of how things are now, so a restore can itself be undone.
                if (safetyBackup) await RunLocked(BackupReason.BeforeRestore, ct);

                var extracted = Path.Combine(WorkFolder, "restore-" + Guid.NewGuid().ToString("N"));
                try
                {
                    ZipFile.ExtractToDirectory(zip, extracted);
                    await dump.Restore(Path.Combine(extracted, "database", dump.FileName), ct);
                    ReplaceData(Path.Combine(extracted, "data"));
                }
                finally
                {
                    TryDeleteFolder(extracted);
                }

                // An older backup is brought up to this version; the keys and pepper it brought take effect now.
                await DatabaseMigrator.Migrate(scopes, ct);
                secrets.Reload();
                setupGate.Forget();
                log.LogWarning("Restored the backup {Name} of {Business} from {CreatedAt}", Path.GetFileName(path), manifest.Business, manifest.CreatedAt);
                return 0;
            }, ct);
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>Swaps the data folder for the backup's, keeping the current one aside until the swap is done.</summary>
    private void ReplaceData(string from)
    {
        var aside = DataFolder.TrimEnd(Path.DirectorySeparatorChar) + ".before-restore";
        TryDeleteFolder(aside);
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(aside);
        string[] keep = ["updates", .. BackupsInsideData().Select(p => p.Split('/')[0])];
        foreach (var item in Directory.EnumerateFileSystemEntries(DataFolder))
        {
            if (keep.Contains(Path.GetFileName(item))) continue;
            var target = Path.Combine(aside, Path.GetFileName(item));
            if (Directory.Exists(item)) Directory.Move(item, target);
            else File.Move(item, target);
        }
        try
        {
            if (Directory.Exists(from)) CopyFolder(from, DataFolder);
            TryDeleteFolder(aside);
        }
        catch
        {
            // Put the old data back so the server keeps working as before.
            foreach (var item in Directory.EnumerateFileSystemEntries(DataFolder).Where(i => !keep.Contains(Path.GetFileName(i)))) DeleteItem(item);
            CopyFolder(aside, DataFolder);
            throw;
        }
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    /// <summary>Runs <paramref name="read"/> on the backup as a plain zip (decrypting it to a temporary file first if needed).</summary>
    private async Task<T> Open<T>(string path, string? password, Func<string, Task<T>> read, CancellationToken ct)
    {
        if (!BackupFile.IsEncrypted(path)) return await read(path);
        Directory.CreateDirectory(WorkFolder);
        var plain = Path.Combine(WorkFolder, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await BackupFile.Decrypt(path, plain, password ?? StoredPassword(), ct);
            return await read(plain);
        }
        finally
        {
            TryDelete(plain);
        }
    }

    private string? StoredPassword()
    {
        var stored = ReadSettings().Password;
        return stored is null ? null : MachineSecrets.Unprotect(stored);
    }

    private static void DeleteItem(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
        else TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
