using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.Common;

namespace Tausi.Application.Settings;

/// <summary>
/// Printing settings in BusinessSettings.OtherJson under "print":
/// <c>{ "print": { "logoOnBill": true, "poweredByFooter": false } }</c>.
/// <c>poweredByFooter</c> adds "Powered by Tausi POS" at the foot of bills and receipts. It is off by
/// default: customer slips carry the business's own name, not ours.
/// </summary>
public sealed record PrintSettings(bool LogoOnBill, bool PoweredByFooter = false)
{
    public static readonly PrintSettings Default = new(true);

    public static PrintSettings From(string? otherJson)
    {
        if (string.IsNullOrWhiteSpace(otherJson)) return Default;
        try
        {
            var print = JsonNode.Parse(otherJson)?["print"];
            return new PrintSettings(
                print?["logoOnBill"]?.GetValue<bool>() ?? Default.LogoOnBill,
                print?["poweredByFooter"]?.GetValue<bool>() ?? Default.PoweredByFooter);
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
        foreach (var name in new[] { "logoOnBill", "poweredByFooter" })
            if (print.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new DomainException($"print.{name} must be true or false.");
    }

    public static async Task<PrintSettings> Load(ITausiDb db, CancellationToken ct) =>
        From(await db.BusinessSettings.AsNoTracking().Select(s => s.OtherJson).FirstOrDefaultAsync(ct));
}
