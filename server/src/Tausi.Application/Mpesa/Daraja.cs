namespace Tausi.Application.Mpesa;

/// <summary>A Daraja call that failed: its words are safe to show (no credentials in them).</summary>
public sealed class DarajaException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record StkPushRequest(
    string ShortCode, string PartyB, bool Paybill, string Phone, int Amount, string CallbackUrl, string AccountReference, string Description);

public sealed record StkPushResult(string CheckoutRequestId, string? MerchantRequestId, string? CustomerMessage);

/// <summary>An STK prompt's state from the query API: <see cref="ResultCode"/> is null while the customer hasn't answered.</summary>
public sealed record StkQueryResult(int? ResultCode, string? ResultDesc);

public sealed record DynamicQrRequest(string MerchantName, string Reference, int Amount, bool Paybill, string PayTo, int Size = 300);

/// <summary>
/// Safaricom's Daraja API: OAuth, STK push and query, Dynamic QR and C2B URL registration.
/// Every call takes the credentials and the environment; the implementation caches the OAuth token.
/// </summary>
public interface IDaraja
{
    /// <summary>Gets an OAuth token (Test connection).</summary>
    Task CheckCredentials(MpesaCredentials credentials, bool live, CancellationToken ct);

    Task<StkPushResult> StkPush(MpesaCredentials credentials, bool live, StkPushRequest request, CancellationToken ct);

    Task<StkQueryResult> StkQuery(MpesaCredentials credentials, bool live, string shortCode, string checkoutRequestId, CancellationToken ct);

    /// <summary>The QR code as a PNG image.</summary>
    Task<byte[]> DynamicQr(MpesaCredentials credentials, bool live, DynamicQrRequest request, CancellationToken ct);

    Task RegisterC2B(MpesaCredentials credentials, bool live, string shortCode, string confirmationUrl, string validationUrl, CancellationToken ct);
}

/// <summary>Daraja's STK result codes in plain words, for the cashier.</summary>
public static class StkResults
{
    public const int Paid = 0;

    public static string Message(int code, string? desc) => code switch
    {
        0 => "Paid.",
        1 => "The customer doesn't have enough money on M-Pesa.",
        1032 => "The customer cancelled the prompt.",
        1037 => "No response from the phone. It may be off or out of reach.",
        2001 => "Wrong PIN. Send the prompt again.",
        1019 => "The prompt expired before the customer answered.",
        1001 => "The customer's phone is busy with another M-Pesa request. Try again in a minute.",
        1025 or 9999 => "M-Pesa couldn't send the prompt. Try again.",
        17 => "M-Pesa refused the payment (limit reached). Try another way to pay.",
        _ => $"M-Pesa didn't take the payment ({code}{(string.IsNullOrWhiteSpace(desc) ? "" : ": " + desc.Trim())}).",
    };

    /// <summary>Whether a code means the prompt is over (anything but "still waiting").</summary>
    public static bool Final(int? code) => code is not null;
}
