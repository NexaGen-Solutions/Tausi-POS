using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Tausi.Application.Mpesa;

namespace Tausi.Infrastructure.Mpesa;

/// <summary>
/// Safaricom Daraja over HTTPS (sandbox or live). OAuth tokens are cached per consumer key until a
/// minute before they expire. Errors become <see cref="DarajaException"/> with Daraja's own words
/// (never the credentials). Times for passwords are Nairobi time, as Daraja expects.
/// </summary>
public sealed class HttpDaraja(TimeProvider clock) : IDaraja
{
    public const string SandboxUrl = "https://sandbox.safaricom.co.ke";
    public const string LiveUrl = "https://api.safaricom.co.ke";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly ConcurrentDictionary<(string Key, bool Live), (string Token, DateTimeOffset Until)> Tokens = new();
    private static readonly JsonSerializerOptions Exact = new();

    private static string Root(bool live) => live ? LiveUrl : SandboxUrl;

    public async Task CheckCredentials(MpesaCredentials credentials, bool live, CancellationToken ct) =>
        await Token(credentials, live, ct, fresh: true);

    public async Task<StkPushResult> StkPush(MpesaCredentials credentials, bool live, StkPushRequest r, CancellationToken ct)
    {
        var (password, timestamp) = Password(r.ShortCode, credentials.Passkey);
        var json = await Post(credentials, live, "/mpesa/stkpush/v1/processrequest", new
        {
            BusinessShortCode = r.ShortCode, Password = password, Timestamp = timestamp,
            TransactionType = r.Paybill ? "CustomerPayBillOnline" : "CustomerBuyGoodsOnline",
            Amount = r.Amount, PartyA = r.Phone, PartyB = r.PartyB, PhoneNumber = r.Phone, CallBackURL = r.CallbackUrl,
            AccountReference = Cut(r.AccountReference, 12), TransactionDesc = Cut(r.Description, 13),
        }, ct);
        if (Text(json, "ResponseCode") != "0" || Text(json, "CheckoutRequestID") is not { } checkout)
            throw new DarajaException(Text(json, "ResponseDescription") ?? Text(json, "errorMessage") ?? "M-Pesa didn't accept the prompt.");
        return new StkPushResult(checkout, Text(json, "MerchantRequestID"), Text(json, "CustomerMessage"));
    }

    public async Task<StkQueryResult> StkQuery(MpesaCredentials credentials, bool live, string shortCode, string checkoutRequestId, CancellationToken ct)
    {
        var (password, timestamp) = Password(shortCode, credentials.Passkey);
        JsonElement json;
        try
        {
            json = await Post(credentials, live, "/mpesa/stkpushquery/v1/query",
                new { BusinessShortCode = shortCode, Password = password, Timestamp = timestamp, CheckoutRequestID = checkoutRequestId }, ct);
        }
        catch (DarajaException e) when (e.Message.Contains("being processed", StringComparison.OrdinalIgnoreCase))
        {
            // "The transaction is being processed": the customer hasn't answered yet.
            return new StkQueryResult(null, null);
        }
        return int.TryParse(Text(json, "ResultCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            ? new StkQueryResult(code, Text(json, "ResultDesc"))
            : new StkQueryResult(null, null);
    }

    public async Task<byte[]> DynamicQr(MpesaCredentials credentials, bool live, DynamicQrRequest r, CancellationToken ct)
    {
        var json = await Post(credentials, live, "/mpesa/qrcode/v1/generate", new
        {
            MerchantName = r.MerchantName, RefNo = r.Reference, Amount = r.Amount, TrxCode = r.Paybill ? "PB" : "BG", CPI = r.PayTo,
            Size = r.Size.ToString(CultureInfo.InvariantCulture),
        }, ct);
        var qr = Text(json, "QRCode") ?? throw new DarajaException(Text(json, "ResponseDescription") ?? "M-Pesa gave no QR code.");
        try
        {
            return Convert.FromBase64String(qr);
        }
        catch (FormatException e)
        {
            throw new DarajaException("M-Pesa's QR code couldn't be read.", e);
        }
    }

    public async Task RegisterC2B(MpesaCredentials credentials, bool live, string shortCode, string confirmationUrl, string validationUrl, CancellationToken ct)
    {
        var json = await Post(credentials, live, "/mpesa/c2b/v2/registerurl",
            new { ShortCode = shortCode, ResponseType = "Completed", ConfirmationURL = confirmationUrl, ValidationURL = validationUrl }, ct);
        var code = Text(json, "ResponseCode") ?? "";
        if (code != "0" && code != "00000000" && !string.Equals(Text(json, "ResponseDescription"), "Success", StringComparison.OrdinalIgnoreCase))
            throw new DarajaException(Text(json, "ResponseDescription") ?? Text(json, "errorMessage") ?? "Safaricom refused the registration.");
    }

    /// <summary>Base64(short code + passkey + timestamp), with the timestamp in Nairobi time.</summary>
    public (string Password, string Timestamp) Password(string shortCode, string passkey)
    {
        var nairobi = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(3));
        var timestamp = nairobi.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return (Convert.ToBase64String(Encoding.UTF8.GetBytes(shortCode + passkey + timestamp)), timestamp);
    }

    private async Task<string> Token(MpesaCredentials c, bool live, CancellationToken ct, bool fresh = false)
    {
        var now = clock.GetUtcNow();
        if (!fresh && Tokens.TryGetValue((c.ConsumerKey, live), out var cached) && cached.Until > now) return cached.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, Root(live) + "/oauth/v1/generate?grant_type=client_credentials");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.ConsumerKey}:{c.ConsumerSecret}")));
        using var response = await Send(request, ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            throw new DarajaException("Safaricom didn't accept the consumer key and secret.");
        var json = await Json(response, ct);
        var token = Text(json, "access_token") ?? throw new DarajaException("Safaricom gave no access token.");
        var seconds = int.TryParse(Text(json, "expires_in"), out var s) ? s : 3599;
        Tokens[(c.ConsumerKey, live)] = (token, now.AddSeconds(Math.Max(60, seconds - 60)));
        return token;
    }

    private async Task<JsonElement> Post(MpesaCredentials c, bool live, string path, object body, CancellationToken ct)
    {
        var token = await Token(c, live, ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, Root(live) + path) { Content = JsonContent.Create(body, options: Exact) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Send(request, ct);
        var json = await Json(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) Tokens.TryRemove((c.ConsumerKey, live), out _);
            throw new DarajaException(Text(json, "errorMessage") ?? Text(json, "ResponseDescription") ?? $"Safaricom answered {(int)response.StatusCode}.");
        }
        return json;
    }

    private static async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await Http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new DarajaException("Can't reach Safaricom. Check the internet connection.", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new DarajaException("Safaricom didn't answer in time.", e);
        }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
