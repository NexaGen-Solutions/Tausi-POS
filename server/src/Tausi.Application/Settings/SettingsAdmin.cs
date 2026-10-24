using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.People;

namespace Tausi.Application.Settings;

public sealed record SettingEntryDto(
    string Key, string Type, string Label, string Help, JsonNode? Value, JsonNode? Default,
    decimal? Min, decimal? Max, IReadOnlyList<string>? Allowed, bool ReloadsTerminals, int MaxLength, bool Required,
    int? Rule, bool Editable);

public sealed record SettingsSectionDto(string Key, string Title, IReadOnlyList<SettingEntryDto> Entries);

/// <summary>
/// The settings screen: every section and setting with its value and default. <c>Version</c> goes back
/// with a save; <c>LogoFileId</c> is the screen-sized logo and <c>LogoPrintUrl</c> its 1-bit print version.
/// </summary>
public sealed record SettingsDto(long Version, IReadOnlyList<SettingsSectionDto> Sections, Guid? LogoFileId, string? LogoPrintUrl);

/// <summary>Only the changed keys, with the version the form was loaded at.</summary>
public sealed record UpdateSettingsRequest(Dictionary<string, JsonElement>? Values, long Version);

public sealed record SettingChangeDto(DateTimeOffset At, string Who, string Key, string Label, string? Old, string? New);

public static class SettingsCodes
{
    public const string Conflict = "settings_conflict";
}

public sealed class GetSettings(ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval)
{
    public async Task<SettingsDto> Execute(CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);
        var row = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("The business has not been set up yet.");
        return SettingsView.Of(row.RowVersion, SettingsRegistry.Read(row), row.LogoWebFileId ?? row.LogoFileId);
    }
}

public static class SettingsView
{
    public const string LogoPrintPath = "/api/admin/settings/logo/print.png";

    public static SettingsDto Of(long version, SettingValues values, Guid? logo) => new(
        version,
        SettingsRegistry.SectionTitles
            .Select(s => new SettingsSectionDto(s.Key, s.Title, SettingsRegistry.All.Where(d => d.Section == s.Key)
                .OrderBy(d => d.Rule ?? 0)
                .Select(d => new SettingEntryDto(
                    d.Key, d.Type.ToString(), d.Label, d.Help, values[d.Key]?.DeepClone(), d.Default?.DeepClone(), d.Min, d.Max,
                    d.Allowed, d.ReloadsTerminals, d.MaxLength, d.Required, d.Rule, d.Editable))
                .ToList()))
            .Where(s => s.Entries.Count > 0)
            .ToList(),
        logo,
        logo is null ? null : $"{LogoPrintPath}?v={logo}");

    /// <summary>Checks the changed values against the registry: unknown keys, the logo and bad values come back per field.</summary>
    public static Dictionary<string, JsonNode?> Check(IReadOnlyDictionary<string, JsonElement>? values)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var clean = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, value) in values ?? new Dictionary<string, JsonElement>())
        {
            var definition = SettingsRegistry.Find(key);
            if (definition is null) errors[key] = "Unknown setting.";
            else if (!definition.Editable) errors[key] = "Upload or remove the logo with its own buttons.";
            else if (SettingsRegistry.Normalize(definition, value, out var normalized) is { } error) errors[key] = error;
            else clean[key] = normalized;
        }
        if (errors.Count > 0) throw new FieldErrorsException(errors);
        return clean;
    }

    /// <summary>A value as the history shows it: text without quotes, lists with commas.</summary>
    public static string? Show(JsonNode? value) => value switch
    {
        null => null,
        JsonArray list => string.Join(", ", list.Select(n => n?.ToJsonString())),
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        _ => value.ToJsonString(),
    };
}

/// <summary>
/// Saves the changed settings at once: refused if someone else saved since the form was loaded,
/// checked against the registry (errors per field), one audit entry per changed key with its old
/// and new value, then <c>settings.changed</c> with the changed keys so terminals reload them.
/// </summary>
public sealed class UpdateSettings(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    IAuditLog audit, IPosEvents events)
{
    public async Task<SettingsDto> Execute(UpdateSettingsRequest request, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);
        var clean = SettingsView.Check(request.Values);

        var row = await db.BusinessSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("The business has not been set up yet.");
        if (row.RowVersion != request.Version)
            throw new ConflictException("Someone else saved settings. Reload to see their changes.", SettingsCodes.Conflict);

        var before = SettingsRegistry.Read(row);
        var changed = new List<string>();
        foreach (var definition in SettingsRegistry.All)
        {
            if (!clean.TryGetValue(definition.Key, out var value) || SettingValues.Same(before[definition.Key], value)) continue;
            SettingsRegistry.Write(row, definition.Key, value);
            audit.Write(new AuditRecord(AuditActions.SettingsChanged, AuditEntities.Setting, definition.Key,
                new { value = before[definition.Key] }, new { value }, grant.ApprovedByUserId));
            changed.Add(definition.Key);
        }

        if (changed.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await events.Publish(PosEventNames.SettingsChanged, new { keys = changed }, ct);
        }
        return SettingsView.Of(row.RowVersion, SettingsRegistry.Read(row), row.LogoWebFileId ?? row.LogoFileId);
    }
}

/// <summary>The last 50 setting changes (the logo included): who, when, which and the old and new value.</summary>
public sealed class SettingsHistory(ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval)
{
    public async Task<IReadOnlyList<SettingChangeDto>> Execute(CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);
        var entries = await db.AuditEntries.AsNoTracking()
            .Where(a => (a.Action == AuditActions.SettingsChanged && a.EntityType == AuditEntities.Setting) || a.Action == AuditActions.LogoChanged)
            .OrderByDescending(a => a.At).Take(50).ToListAsync(ct);
        var userIds = entries.Where(e => e.UserId != null).Select(e => e.UserId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return entries.Select(e =>
        {
            var key = e.Action == AuditActions.LogoChanged ? SettingsRegistry.BusinessLogo : e.EntityId ?? "";
            return new SettingChangeDto(
                e.At, e.UserId is { } id ? names.GetValueOrDefault(id, "?") : "?", key, SettingsRegistry.Find(key)?.Label ?? key,
                Value(e.OldValueJson), Value(e.NewValueJson));
        }).ToList();
    }

    private static string? Value(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var node = JsonNode.Parse(json) as JsonObject;
        return SettingsView.Show(node?["value"] ?? node?["logoFileId"]);
    }
}
