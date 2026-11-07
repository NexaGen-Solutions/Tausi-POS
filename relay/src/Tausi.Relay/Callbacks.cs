using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Tausi.Relay;

/// <summary>
/// Safaricom's callbacks, one set per business under its path token: STK results and C2B
/// (till, paybill and QR) validation and confirmation. Only Safaricom's addresses with a known
/// token are heard; anything else is logged and refused. Answers come at once, in Daraja's format.
/// The paths avoid the word "mpesa": Daraja refuses callback addresses that contain it.
/// </summary>
public static class Callbacks
{
    public static readonly object Accepted = new { ResultCode = 0, ResultDesc = "Accepted" };

    /// <summary>Daraja's answer keeps its own names ("ResultCode"), not camelCase.</summary>
    private static IResult Ok() => Results.Json(Accepted, new JsonSerializerOptions());

    public static void MapCallbacks(this WebApplication app)
    {
        var group = app.MapGroup("/pay/{token}");
        group.MapPost("/stk", (string token, JsonElement body, HttpContext http, CancellationToken ct) =>
            Receive(token, http, () => ParseStk(body), ct));
        group.MapPost("/c2b/validation", async (string token, HttpContext http, CancellationToken ct) =>
            await Check(token, http, ct) is { Result: { } refused } ? refused : Ok());
        group.MapPost("/c2b/confirmation", (string token, JsonElement body, HttpContext http, CancellationToken ct) =>
            Receive(token, http, () => ParseC2B(body), ct));
    }

    private static async Task<IResult> Receive(string token, HttpContext http, Func<RelayPayment?> parse, CancellationToken ct)
    {
        var check = await Check(token, http, ct);
        if (check is { Result: { } refused }) return refused;
        var payment = parse();
        if (payment is null)
        {
            Log(http).LogWarning("Unreadable callback for {Business} on {Path}", check!.Business!.Name, http.Request.Path);
            return Ok();
        }
        payment.BusinessId = check!.Business!.Id;
        await http.RequestServices.GetRequiredService<Store>().Save(payment, ct);
        return Ok();
    }

    private sealed record Checked(Business? Business, IResult? Result);

    /// <summary>The business when the caller is Safaricom with a known token; else the refusal.</summary>
    private static async Task<Checked?> Check(string token, HttpContext http, CancellationToken ct)
    {
        var options = http.RequestServices.GetRequiredService<IOptions<RelayOptions>>().Value;
        var from = http.Connection.RemoteIpAddress;
        if (from is null || !options.SafaricomRanges.Any(r => r.Contains(from)))
        {
            Log(http).LogWarning("Refused a callback from {Address}: not one of Safaricom's addresses", from);
            return new Checked(null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }
        var db = http.RequestServices.GetRequiredService<RelayDb>();
        var business = await db.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.PathToken == token && b.Active, ct);
        if (business is not null) return new Checked(business, null);
        Log(http).LogWarning("Refused a callback from {Address} with an unknown token", from);
        return new Checked(null, Results.NotFound());
    }

    private static ILogger Log(HttpContext http) => http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Tausi.Relay.Callbacks");

    /// <summary>Daraja's STK callback: {"Body":{"stkCallback":{...,"CallbackMetadata":{"Item":[...]}}}}.</summary>
    public static RelayPayment? ParseStk(JsonElement body)
    {
        if (!body.TryGetProperty("Body", out var b) || !b.TryGetProperty("stkCallback", out var s)) return null;
        var checkout = Text(s, "CheckoutRequestID");
        if (checkout is null) return null;
        var items = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (s.TryGetProperty("CallbackMetadata", out var meta) && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("Item", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                if (Text(item, "Name") is { } name && item.TryGetProperty("Value", out var value))
                    items[name] = value;
        var receipt = items.TryGetValue("MpesaReceiptNumber", out var r) ? r.ToString() : null;
        return new RelayPayment
        {
            Kind = PaymentKinds.Stk,
            Key = receipt ?? "stk:" + checkout,
            TransactionId = receipt,
            CheckoutRequestId = checkout,
            MerchantRequestId = Text(s, "MerchantRequestID"),
            ResultCode = s.TryGetProperty("ResultCode", out var code) && int.TryParse(code.ToString(), out var c) ? c : null,
            ResultDesc = Cut(Text(s, "ResultDesc"), 300),
            Amount = items.TryGetValue("Amount", out var a) && decimal.TryParse(a.ToString(), CultureInfo.InvariantCulture, out var amount) ? amount : 0,
            Phone = Phones.Mask(items.TryGetValue("PhoneNumber", out var p) ? p.ToString() : null),
            PaidAt = items.TryGetValue("TransactionDate", out var d) ? Time(d.ToString()) : null,
        };
    }

    /// <summary>Daraja's C2B confirmation (also for a QR payment).</summary>
    public static RelayPayment? ParseC2B(JsonElement body)
    {
        var id = Text(body, "TransID");
        if (string.IsNullOrWhiteSpace(id) || !decimal.TryParse(Text(body, "TransAmount"), CultureInfo.InvariantCulture, out var amount)) return null;
        var name = string.Join(' ', new[] { Text(body, "FirstName"), Text(body, "MiddleName"), Text(body, "LastName") }
            .Where(n => !string.IsNullOrWhiteSpace(n)));
        return new RelayPayment
        {
            Kind = PaymentKinds.C2B,
            Key = id.Trim().ToUpperInvariant(),
            TransactionId = id.Trim().ToUpperInvariant(),
            Amount = amount,
            PayerName = Cut(name, 120),
            Phone = Phones.Mask(Text(body, "MSISDN")),
            ShortCode = Cut(Text(body, "BusinessShortCode"), 20),
            BillRef = Cut(Text(body, "BillRefNumber")?.Trim(), 40) is { Length: > 0 } bill ? bill : null,
            TransactionType = Cut(Text(body, "TransactionType"), 40),
            PaidAt = Time(Text(body, "TransTime")),
        };
    }

    /// <summary>Safaricom's "20261105214712", Nairobi time (UTC+3), as UTC.</summary>
    public static DateTime? Time(string? text) =>
        DateTime.TryParseExact(text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? DateTime.SpecifyKind(local.AddHours(-3), DateTimeKind.Utc)
            : null;

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}
