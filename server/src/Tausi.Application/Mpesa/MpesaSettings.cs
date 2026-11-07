using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Settings;
using Tausi.Domain.Mpesa;
using static Tausi.Application.Settings.SettingsRegistry;

namespace Tausi.Application.Mpesa;

/// <summary>Encrypts secrets kept in the database (the server's data-protection keys, which stay on this PC).</summary>
public interface ISecretProtector
{
    string Protect(string plain);

    /// <summary>Null when it can't be read (written on another PC, e.g. after a restore elsewhere).</summary>
    string? Unprotect(string protectedText);
}

/// <summary>The Daraja credentials, decrypted, for one call. Never logged or returned.</summary>
public sealed record MpesaCredentials(string ConsumerKey, string ConsumerSecret, string Passkey);

/// <summary>
/// The M-Pesa settings ("mpesa.*") with the secrets. <see cref="SetUp"/> is when Daraja can be used:
/// the numbers and the three credentials are there. Prompts and the QR need it; typed codes don't.
/// </summary>
public sealed record MpesaSettings(
    bool Paybill, string Till, string Store, string PaybillNumber, bool Live, string RelayUrl,
    bool StkEnabled, bool WaiterPrompts, bool QrOnBill, bool AllowTypedCodes,
    MpesaCredentials? Credentials, string? RelayApiKey, bool SecretsUnreadable, string StkCallbackUrl)
{
    /// <summary>Where customers pay: the till, or the paybill.</summary>
    public string PayTo => Paybill ? PaybillNumber : Till;

    /// <summary>Daraja's BusinessShortCode for STK and C2B: the store (head office) number for Buy Goods, the paybill for Paybill.</summary>
    public string ShortCode => Paybill ? PaybillNumber : Store;

    public bool SetUp => Credentials is not null && PayTo.Length > 0 && ShortCode.Length > 0;

    public bool RelaySetUp => SetUp && !string.IsNullOrEmpty(RelayApiKey) && RelayUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    public static async Task<MpesaSettings> Load(ITausiDb db, ISecretProtector secrets, CancellationToken ct)
    {
        var s = await SettingsRegistry.Load(db, ct);
        var account = await db.MpesaAccounts.AsNoTracking().OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
        var unreadable = false;
        string? Read(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            var plain = secrets.Unprotect(stored);
            if (plain is null) unreadable = true;
            return plain;
        }

        var key = Read(account?.ConsumerKey);
        var secret = Read(account?.ConsumerSecret);
        var passkey = Read(account?.Passkey);
        var relay = Read(account?.RelayApiKey);
        return new MpesaSettings(
            s.String(MpesaType) == "Paybill", s.String(MpesaTill).Trim(), s.String(MpesaStore).Trim(), s.String(MpesaPaybill).Trim(),
            s.String(MpesaEnvironment) == "Live", s.String(MpesaRelayUrl).Trim().TrimEnd('/'),
            s.Bool(MpesaStkEnabled), s.Bool(MpesaWaiterPrompts), s.Bool(MpesaQrOnBill), s.Bool(MpesaAllowTypedCodes),
            key is not null && secret is not null && passkey is not null ? new MpesaCredentials(key, secret, passkey) : null,
            relay, unreadable,
            // Until Test connection has asked the relay for this business's address, Daraja still needs one: the query route answers instead.
            account?.StkCallbackUrl ?? s.String(MpesaRelayUrl).Trim().TrimEnd('/') + "/pay/not-registered/stk");
    }
}

/// <summary>Kenyan phone numbers for STK: 07…, 01…, +254… and 254… become 2547… / 2541….</summary>
public static class MpesaPhones
{
    public static string? Normalise(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("254") && digits.Length == 12) digits = digits[3..];
        else if (digits.StartsWith('0') && digits.Length == 10) digits = digits[1..];
        return digits.Length == 9 && (digits[0] == '7' || digits[0] == '1') ? "254" + digits : null;
    }

    /// <summary>"0712 ••• 678".</summary>
    public static string Mask(string normalised) => $"0{normalised[3..6]} ••• {normalised[^3..]}";
}
