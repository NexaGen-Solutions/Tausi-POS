using System.Text.Json;
using System.Text.Json.Nodes;
using Tausi.Application.Printing;
using Tausi.Domain.Common;

namespace Tausi.Application.Settings;

/// <summary>
/// The optional "Powered by Tausi POS" line at the foot of customer slips, kept in
/// BusinessSettings.OtherJson as <c>{ "print": { "poweredByFooter": true } }</c>. Off by default:
/// customer slips carry the business's own name, not ours.
/// </summary>
public static class PoweredByFooter
{
    public const string Text = "Powered by Tausi POS";

    public static bool IsOn(string? otherJson)
    {
        if (string.IsNullOrWhiteSpace(otherJson)) return false;
        try
        {
            return JsonNode.Parse(otherJson)?["print"]?["poweredByFooter"]?.GetValue<bool>() ?? false;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Checks <c>print.poweredByFooter</c> in an OtherJson document an admin wants to save.</summary>
    public static void Validate(JsonElement root)
    {
        if (!root.TryGetProperty("print", out var print) || print.ValueKind != JsonValueKind.Object) return;
        if (print.TryGetProperty("poweredByFooter", out var value)
            && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new DomainException("print.poweredByFooter must be true or false.");
    }

    /// <summary>Adds the line to a customer slip when the setting is on.</summary>
    public static ReceiptBuilder AddTo(ReceiptBuilder builder, bool on) =>
        on ? builder.Feed().Text(Text, TextStyle.Center) : builder;
}
