using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Settings;
using static Tausi.Application.Settings.SettingsRegistry;

namespace Tausi.Application.Settings;

/// <param name="Width">42 (80 mm paper) or 32 (58 mm).</param>
/// <param name="Values">Unsaved changes from the form, checked like a save; the rest are the saved values.</param>
public sealed record ReceiptPreviewRequest(int Width, Dictionary<string, JsonElement>? Values = null);

/// <summary>The sample bill and receipt as printed text, and the logo as the printer prints it (a PNG, base64) if it prints.</summary>
public sealed record ReceiptPreviewDto(int Width, string Bill, string Receipt, string? LogoPng);

/// <summary>
/// A sample bill and receipt with the current settings, including unsaved values, made by the same
/// slip code as real ones: the business header, logo, footer, VAT, rounding, a voided item when voids
/// show on the receipt, and the "brought by" line when it is recorded.
/// </summary>
public sealed class ReceiptPreview(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    ILogoProvider logos, ILogoImages images, TimeProvider clock)
{
    public async Task<ReceiptPreviewDto> Execute(ReceiptPreviewRequest request, CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);
        if (request.Width is not (42 or 32)) throw new DomainException("Choose 42 or 32 columns.");
        var row = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct) ?? new BusinessSettings();
        var values = SettingsRegistry.Read(row).With(SettingsView.Check(request.Values));

        var paperMm = request.Width == 32 ? 58 : 80;
        var print = PrintSettings.From(values);
        var logo = print.LogoOnBill ? await logos.GetLogo(ReceiptBuilder.LogoWidthFor(paperMm), ct) : null;
        var at = BusinessClock.ToLocal(clock.GetUtcNow(), BusinessTimeZone.Find(values.String(BusinessTimeZoneKey)));
        var (bill, receipt) = Render(values, request.Width, logo, at);
        return new ReceiptPreviewDto(request.Width, bill.Text, receipt.Text, logo is null ? null : Convert.ToBase64String(images.ToPng(logo)));
    }

    /// <summary>The sample slips for these settings (also used by the golden tests).</summary>
    public static (PrintDocument Bill, PrintDocument Receipt) Render(SettingValues values, int columns, MonoBitmap? logo, DateTimeOffset at)
    {
        var print = PrintSettings.From(values);
        var currency = values.String(BusinessCurrency);
        var style = new SlipStyle(columns, true, logo, currency, print.PoweredByFooter);
        var business = new BusinessInfo(
            values.String(BusinessName), values.String(BusinessAddress), values.String(BusinessPhone), values.String(BusinessReceiptFooter));
        var lines = Slips.SampleLines(values.Decimal(BusinessVatRate));
        var bill = Slips.Bill(new BillSlip(business, 1, 1042, false, "Table 7", "edna", "Counter A", at, lines), style);

        var receiptLines = print.ShowVoidsOnReceipt
            ? lines.Append(new SlipLine(1, "Guiness", null, 0, 300m, values.Decimal(BusinessVatRate), IsVoid: true)).ToList()
            : lines;
        var total = lines.Sum(l => l.Amount);
        var rounded = CashRounding.Apply(total, values.Enum<RoundingRule>(MoneyRounding));
        var tendered = Math.Ceiling(rounded.Rounded / 500m) * 500m;
        var payment = new ReceiptPayment("Cash", rounded.Rounded, tendered, tendered - rounded.Rounded, null, "edna");
        var receipt = Slips.Receipt(
            new ReceiptSlip(business, 2041, 1042, false, "Table 7", "edna", "CAROL", "Counter A", at, receiptLines, null,
                rounded.Difference, [payment], ShowBroughtBy: PaymentSettings.From(values).RecordBroughtBy),
            style);
        return (bill, receipt);
    }
}
