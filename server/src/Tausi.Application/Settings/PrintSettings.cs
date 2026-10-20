using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.Common;

namespace Tausi.Application.Settings;

/// <summary>Printing settings in BusinessSettings.OtherJson under "print": <c>{ "print": { "logoOnBill": true } }</c>.</summary>
public sealed record PrintSettings(bool LogoOnBill)
{
    public static readonly PrintSettings Default = new(true);

    public static PrintSettings From(string? otherJson)
    {
        if (string.IsNullOrWhiteSpace(otherJson)) return Default;
        try
        {
            return new PrintSettings(JsonNode.Parse(otherJson)?["print"]?["logoOnBill"]?.GetValue<bool>() ?? true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Default;
        }
    }

    public static void Validate(JsonElement root)
    {
        if (!root.TryGetProperty("print", out var print)) return;
        if (print.ValueKind != JsonValueKind.Object) throw new DomainException("\"print\" settings must be a JSON object.");
        if (print.TryGetProperty("logoOnBill", out var logo) && logo.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new DomainException("print.logoOnBill must be true or false.");
    }

    public static async Task<PrintSettings> Load(ITausiDb db, CancellationToken ct) =>
        From(await db.BusinessSettings.AsNoTracking().Select(s => s.OtherJson).FirstOrDefaultAsync(ct));
}
