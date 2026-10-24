using Tausi.Application.Abstractions;
using Tausi.Domain.Settings;
using static Tausi.Application.Settings.SettingsRegistry;

namespace Tausi.Application.Settings;

/// <summary>
/// Printing settings (the "print.*" keys of <see cref="SettingsRegistry"/>).
/// <c>PoweredByFooter</c> adds "Powered by Tausi POS" at the foot of bills and receipts. It is off by
/// default: customer slips carry the business's own name, not ours. <c>ShowVoidsOnReceipt</c> lists
/// voided items on the receipt at 0, marked VOID.
/// </summary>
public sealed record PrintSettings(bool LogoOnBill, bool PoweredByFooter = false, bool ShowVoidsOnReceipt = false)
{
    public static readonly PrintSettings Default = From(SettingsRegistry.Read(null));

    public static PrintSettings From(SettingValues v) =>
        new(v.Bool(PrintLogoOnBill), v.Bool(PrintPoweredByFooter), v.Bool(PrintShowVoidsOnReceipt));

    public static PrintSettings From(string? otherJson) => From(SettingsRegistry.Read(new BusinessSettings { OtherJson = otherJson ?? "" }));

    public static async Task<PrintSettings> Load(ITausiDb db, CancellationToken ct) => From(await SettingsRegistry.Load(db, ct));
}

/// <summary>
/// The rules for taking payment: <c>MpesaCodeRequired</c> (off: the code is optional, but a code
/// that is given is still checked and must be unused) and <c>RecordBroughtBy</c> (off: the waiter
/// who opened the ticket is recorded and the receipt has no "brought by" line). With
/// <c>CloseVoidedWithoutReceipt</c> a ticket whose every line is voided or cancelled closes without a receipt.
/// </summary>
public sealed record PaymentSettings(bool MpesaCodeRequired, bool RecordBroughtBy, bool CloseVoidedWithoutReceipt)
{
    public static PaymentSettings From(SettingValues v) =>
        new(v.Bool(PaymentsMpesaCodeRequired), v.Bool(PaymentsRecordBroughtBy), v.Bool(TicketsCloseVoidedWithoutReceipt));

    public static async Task<PaymentSettings> Load(ITausiDb db, CancellationToken ct) => From(await SettingsRegistry.Load(db, ct));
}
