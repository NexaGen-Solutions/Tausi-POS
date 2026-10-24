using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.Common;

namespace Tausi.Application.Settings;

/// <summary>
/// Cash drawer settings in BusinessSettings.OtherJson:
/// <code>{ "cash": { "defaultFloat": 0, "denominations": [1000, 500, 200, 100, 50] }, "cashup": { "blind": true } }</code>
/// The count has one row per note in <see cref="Denominations"/>, plus coins as one amount. With
/// <see cref="BlindCount"/> on, the cashier sees the expected cash and the difference only after
/// submitting the count.
/// </summary>
public sealed record CashSettings(decimal DefaultFloat, IReadOnlyList<int> Denominations, bool BlindCount)
{
    public static readonly IReadOnlyList<int> KenyanNotes = [1000, 500, 200, 100, 50];
    public static readonly CashSettings Default = new(0m, KenyanNotes, true);

    public static CashSettings From(string? otherJson)
    {
        if (string.IsNullOrWhiteSpace(otherJson)) return Default;
        try
        {
            var root = JsonNode.Parse(otherJson);
            var cash = root?["cash"];
            var notes = cash?["denominations"]?.AsArray().Select(n => n!.GetValue<int>()).Where(n => n > 0).Distinct()
                .OrderByDescending(n => n).ToList();
            return new CashSettings(
                Math.Max(0m, cash?["defaultFloat"]?.GetValue<decimal>() ?? 0m),
                notes is { Count: > 0 } ? notes : KenyanNotes,
                root?["cashup"]?["blind"]?.GetValue<bool>() ?? true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Default;
        }
    }

    public static async Task<CashSettings> Load(ITausiDb db, CancellationToken ct) =>
        From(await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).Select(s => s.OtherJson).FirstOrDefaultAsync(ct));

    public static void Validate(JsonElement root)
    {
        if (root.TryGetProperty("cash", out var cash))
        {
            if (cash.ValueKind != JsonValueKind.Object) throw new DomainException("\"cash\" settings must be a JSON object.");
            if (cash.TryGetProperty("defaultFloat", out var f) && (f.ValueKind != JsonValueKind.Number || f.GetDecimal() < 0))
                throw new DomainException("cash.defaultFloat must be an amount of 0 or more.");
            if (cash.TryGetProperty("denominations", out var d)
                && (d.ValueKind != JsonValueKind.Array || d.GetArrayLength() == 0
                    || d.EnumerateArray().Any(n => n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var v) || v <= 0)))
                throw new DomainException("cash.denominations must be a list of note values, e.g. [1000, 500, 200, 100, 50].");
        }
        if (root.TryGetProperty("cashup", out var cashup))
        {
            if (cashup.ValueKind != JsonValueKind.Object) throw new DomainException("\"cashup\" settings must be a JSON object.");
            if (cashup.TryGetProperty("blind", out var b) && b.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new DomainException("cashup.blind must be true or false.");
        }
    }
}
