using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.Common;

namespace Tausi.Application.Settings;

/// <summary>
/// Terminal behaviour settings, kept in BusinessSettings.OtherJson under "pos":
/// <code>{ "pos": { "idleLogoutMinutes": 10, "logoutAfterSettle": false, "tableIdleMinutes": 30 } }</code>
/// Missing values fall back to the defaults. <see cref="TableIdleMinutes"/> is how long a table's
/// open ticket can go without activity before the table map shows it as unpaid and idle.
/// </summary>
public sealed record PosSettings(int IdleLogoutMinutes, bool LogoutAfterSettle, int TableIdleMinutes = PosSettings.DefaultTableIdleMinutes)
{
    public const int DefaultIdleLogoutMinutes = 10;
    public const int MaxIdleLogoutMinutes = 24 * 60;
    public const int DefaultTableIdleMinutes = 30;
    public const int MaxTableIdleMinutes = 24 * 60;

    public static readonly PosSettings Default = new(DefaultIdleLogoutMinutes, false);

    public TimeSpan IdleLimit => TimeSpan.FromMinutes(IdleLogoutMinutes);

    public TimeSpan TableIdleLimit => TimeSpan.FromMinutes(TableIdleMinutes);

    public static PosSettings From(string? otherJson)
    {
        if (string.IsNullOrWhiteSpace(otherJson)) return Default;
        try
        {
            var pos = JsonNode.Parse(otherJson)?["pos"];
            var idle = pos?["idleLogoutMinutes"]?.GetValue<int>() ?? DefaultIdleLogoutMinutes;
            var afterSettle = pos?["logoutAfterSettle"]?.GetValue<bool>() ?? false;
            var tableIdle = pos?["tableIdleMinutes"]?.GetValue<int>() ?? DefaultTableIdleMinutes;
            return new PosSettings(
                Math.Clamp(idle, 1, MaxIdleLogoutMinutes), afterSettle, Math.Clamp(tableIdle, 1, MaxTableIdleMinutes));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Default;
        }
    }

    /// <summary>Checks the "pos" section of an OtherJson document an admin wants to save.</summary>
    public static void Validate(JsonElement root)
    {
        if (!root.TryGetProperty("pos", out var pos)) return;
        if (pos.ValueKind != JsonValueKind.Object)
            throw new DomainException("\"pos\" settings must be a JSON object.");
        if (pos.TryGetProperty("idleLogoutMinutes", out var idle)
            && (idle.ValueKind != JsonValueKind.Number || !idle.TryGetInt32(out var minutes) || minutes is < 1 or > MaxIdleLogoutMinutes))
            throw new DomainException($"pos.idleLogoutMinutes must be a whole number from 1 to {MaxIdleLogoutMinutes}.");
        if (pos.TryGetProperty("logoutAfterSettle", out var settle)
            && settle.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new DomainException("pos.logoutAfterSettle must be true or false.");
        if (pos.TryGetProperty("tableIdleMinutes", out var tableIdle)
            && (tableIdle.ValueKind != JsonValueKind.Number || !tableIdle.TryGetInt32(out var tableMinutes)
                || tableMinutes is < 1 or > MaxTableIdleMinutes))
            throw new DomainException($"pos.tableIdleMinutes must be a whole number from 1 to {MaxTableIdleMinutes}.");
    }

    public static async Task<PosSettings> Load(ITausiDb db, CancellationToken ct) =>
        From(await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).Select(s => s.OtherJson).FirstOrDefaultAsync(ct));

    /// <summary>The OtherJson a new business starts with, so the settings are easy to find.</summary>
    public static string DefaultJson =>
        JsonSerializer.Serialize(new
        {
            pos = new { idleLogoutMinutes = DefaultIdleLogoutMinutes, logoutAfterSettle = false, tableIdleMinutes = DefaultTableIdleMinutes },
            print = new { logoOnBill = true, poweredByFooter = false },
        });
}
