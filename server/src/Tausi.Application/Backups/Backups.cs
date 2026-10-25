using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.People;

namespace Tausi.Application.Backups;

public enum BackupReason
{
    Nightly,
    Manual,
    Upgrade,
    BeforeRestore,
    Command,
}

/// <summary>One .tausibak in the backups folder, as the list shows it.</summary>
public sealed record BackupRecord(
    string Name,
    DateTimeOffset CreatedAt,
    BackupReason Reason,
    long Size,
    bool Verified,
    bool Encrypted,
    string? Business,
    string Version,
    string? CopiedTo,
    string? CopyError);

/// <summary>
/// What the banners say: <c>ok</c>; <c>failed</c> (the last backup failed); <c>overdue</c> (none
/// succeeded for 48 hours); <c>copyFailed</c> (the backup worked, its second copy didn't); <c>none</c>
/// (no backup yet, on a server younger than 48 hours).
/// </summary>
public sealed record BackupHealth(string State, DateTimeOffset? LastSuccessAt, string? Message)
{
    public static readonly TimeSpan Overdue = TimeSpan.FromHours(48);
    public bool NeedsAttention => State is "failed" or "overdue" or "copyFailed";
}

/// <summary>The schedule (business time), the second-copy folder and encryption. The password itself is never sent back.</summary>
public sealed record BackupSettingsDto(string Time, string? CopyFolder, bool Encrypt, bool HasPassword);

public sealed record SaveBackupSettingsRequest(string Time, string? CopyFolder, bool Encrypt, string? Password);

/// <summary>What a backup holds, read from its manifest before restoring it.</summary>
public sealed record BackupContents(string Business, DateTimeOffset CreatedAt, string Version, string Schema, string Provider, bool Encrypted);

public sealed record RestoreRequest(string Name, string? Password, string? ConfirmBusiness);

/// <summary>Backups and restores: the database, the data folder (keys and uploads) and the config, in one file.</summary>
public interface IBackupEngine
{
    Task<BackupRecord> Run(BackupReason reason, CancellationToken ct);
    IReadOnlyList<BackupRecord> List();
    BackupHealth Health(DateTimeOffset now);
    BackupSettingsDto Settings();
    void SaveSettings(SaveBackupSettingsRequest request);

    /// <summary>The full path of a backup in the list, or null when there is none by that name.</summary>
    string? PathOf(string name);

    Task<BackupContents> Inspect(string path, string? password, CancellationToken ct);

    /// <summary>Takes a safety backup (unless told not to), then restores the file's database and data folder, and migrates.</summary>
    Task Restore(string path, string? password, CancellationToken ct, bool safetyBackup = true);
}

/// <summary>
/// While a backup is restored the POS stops: every API call but health answers 503 "Server is
/// restoring a backup", and terminals show that until it is done.
/// </summary>
public sealed class MaintenanceGate
{
    private volatile string? _message;

    public string? Message => _message;

    public IDisposable Begin(string message)
    {
        if (Interlocked.CompareExchange(ref _message, message, null) is not null)
            throw new ConflictException("A restore is already running.", "restore_running");
        return new End(this);
    }

    private sealed class End(MaintenanceGate gate) : IDisposable
    {
        public void Dispose() => gate._message = null;
    }
}

public static class BackupCodes
{
    public const string WrongPassword = "backup_password";
    public const string OtherBusiness = "backup_other_business";
    public const string Invalid = "backup_invalid";
    public const string Failed = "backup_failed";
}

/// <summary>Settings → Backups (needs <c>settings.manage</c>): the list, the settings, Back up now, Download and Restore.</summary>
public sealed class BackupAdmin(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IBackupEngine engine, IAuditLog audit,
    IPosEvents events, TimeProvider clock)
{
    public const string RestoringMessage = "Server is restoring a backup";

    private async Task Require(CancellationToken ct) =>
        await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, null, ct);

    public async Task<IReadOnlyList<BackupRecord>> List(CancellationToken ct)
    {
        await Require(ct);
        return engine.List();
    }

    public async Task<BackupSettingsDto> Settings(CancellationToken ct)
    {
        await Require(ct);
        return engine.Settings();
    }

    public async Task<BackupSettingsDto> SaveSettings(SaveBackupSettingsRequest request, CancellationToken ct)
    {
        await Require(ct);
        var errors = new Dictionary<string, string>();
        if (!TimeOnly.TryParseExact(request.Time, "HH:mm", out _)) errors["time"] = "Enter a time like 05:00.";
        if (!string.IsNullOrWhiteSpace(request.CopyFolder) && !Path.IsPathRooted(request.CopyFolder))
            errors["copyFolder"] = @"Enter a full folder path, e.g. E:\Backups or \\NAS\Tausi.";
        var old = engine.Settings();
        if (request.Encrypt && !old.HasPassword && string.IsNullOrEmpty(request.Password))
            errors["password"] = "Choose a password for the backups.";
        if (!string.IsNullOrEmpty(request.Password) && request.Password.Length < 8)
            errors["password"] = "At least 8 characters.";
        if (errors.Count > 0) throw new FieldErrorsException(errors);

        engine.SaveSettings(request with { CopyFolder = string.IsNullOrWhiteSpace(request.CopyFolder) ? null : request.CopyFolder.Trim() });
        var saved = engine.Settings();
        audit.Write(new AuditRecord(AuditActions.BackupSettingsChanged, AuditEntities.Setting, "backups",
            OldValue: old, NewValue: saved with { HasPassword = saved.HasPassword }));
        await db.SaveChangesAsync(ct);
        return saved;
    }

    public async Task<BackupRecord> RunNow(CancellationToken ct)
    {
        await Require(ct);
        var record = await engine.Run(BackupReason.Manual, ct);
        audit.Write(new AuditRecord(AuditActions.BackupTaken, "Backup", record.Name, NewValue: new { record.Size, record.Verified, record.CopiedTo }));
        await db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<string> Download(string name, CancellationToken ct)
    {
        await Require(ct);
        var path = engine.PathOf(name) ?? throw new NotFoundException("There is no backup by that name.");
        audit.Write(new AuditRecord(AuditActions.BackupDownloaded, "Backup", name));
        await db.SaveChangesAsync(ct);
        return path;
    }

    public async Task<BackupContents> Inspect(string name, string? password, CancellationToken ct)
    {
        await Require(ct);
        var path = engine.PathOf(name) ?? throw new NotFoundException("There is no backup by that name.");
        return await engine.Inspect(path, password, ct);
    }

    /// <summary>
    /// Restores a backup in the list. One from another business needs its name typed. The audit entry
    /// is written into the restored database afterwards, so it survives the restore.
    /// </summary>
    public async Task Restore(RestoreRequest request, CancellationToken ct)
    {
        var user = session.RequireUser();
        await Require(ct);
        var path = engine.PathOf(request.Name) ?? throw new NotFoundException("There is no backup by that name.");
        var contents = await engine.Inspect(path, request.Password, ct);
        var current = (await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessName);
        if (!SameName(contents.Business, current) && !SameName(contents.Business, request.ConfirmBusiness))
            throw new DomainException(
                $"This backup is from '{contents.Business}', not '{current}'. Type '{contents.Business}' to restore it anyway.",
                BackupCodes.OtherBusiness);

        await events.Publish(PosEventNames.ServerRestoring, new { }, ct);
        await engine.Restore(path, request.Password, ct);

        // Written straight into the restored database: the user (and terminal) of this session may not exist in it.
        var known = await db.Users.AnyAsync(u => u.Id == user.UserId, ct);
        db.AuditEntries.Add(new Domain.Audit.AuditEntry
        {
            At = clock.GetUtcNow(),
            UserId = known ? user.UserId : null,
            Actor = known ? null : user.Name,
            Action = AuditActions.BackupRestored,
            EntityType = "Backup",
            EntityId = request.Name,
            NewValueJson = System.Text.Json.JsonSerializer.Serialize(new { contents.Business, contents.CreatedAt, contents.Version, by = user.Name }),
        });
        await db.SaveChangesAsync(ct);
        await events.Publish(PosEventNames.ServerRestored, new { }, ct);
    }

    public static bool SameName(string? a, string? b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a);

    private static string Normalise(string? s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>The backup banner for any signed-in user: the admin site and the Close day screen.</summary>
public sealed class BackupStatus(ICurrentSession session, IBackupEngine engine, TimeProvider clock)
{
    public BackupHealth Get()
    {
        session.RequireUser();
        return engine.Health(clock.GetUtcNow());
    }
}
