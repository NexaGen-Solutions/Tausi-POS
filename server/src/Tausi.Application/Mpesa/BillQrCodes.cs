using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tausi.Application.Abstractions;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Domain.Service;

namespace Tausi.Application.Mpesa;

/// <summary>Turns a PNG (Daraja's QR) into a crisp 1-bit raster of the given width.</summary>
public interface IMonoImages
{
    MonoBitmap? FromPng(byte[] png, int widthDots);
}

/// <summary>The "Scan to pay" section of a bill (board 26): the QR when Daraja gave one in time, and the till line always.</summary>
public sealed record BillQr(MonoBitmap? Image, bool Paybill, string PayTo, decimal Amount, long TicketNumber);

/// <summary>QR images already made, per ticket, bill number and amount (a reprint uses the same one).</summary>
public sealed class BillQrCache
{
    private const int Max = 500;
    private readonly ConcurrentDictionary<(Guid Ticket, int Bill, decimal Amount, int Width), MonoBitmap> _images = new();

    public MonoBitmap? Get((Guid, int, decimal, int) key) => _images.GetValueOrDefault(key);

    public void Put((Guid, int, decimal, int) key, MonoBitmap image)
    {
        if (_images.Count >= Max) _images.Clear();
        _images[key] = image;
    }
}

/// <summary>
/// The QR for a bill when <c>mpesa.qrOnBill</c> is on and M-Pesa is set up: Daraja's Dynamic QR for
/// what the bill still owes (whole shillings, rounded up), with the ticket number as its reference.
/// Printing never waits more than 3 seconds for it: without an answer the bill prints with the till
/// line only. Not on a training terminal.
/// </summary>
public sealed class BillQrCodes(
    ITausiDb db, ICurrentSession session, ISecretProtector secrets, IDaraja daraja, IMonoImages images, BillQrCache cache, ILogger<BillQrCodes> log)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>The QR's width on paper: 256 dots on 80 mm, 192 on 58 mm.</summary>
    public static int WidthFor(int paperWidthMm) => paperWidthMm == 58 ? 192 : 256;

    public async Task<BillQr?> For(Ticket ticket, decimal remaining, int paperWidthMm, CancellationToken ct)
    {
        if (session.Terminal?.Training == true || remaining <= 0) return null;
        var settings = await MpesaSettings.Load(db, secrets, ct);
        if (!settings.SetUp || !settings.QrOnBill) return null;
        var amount = Math.Ceiling(remaining);
        var width = WidthFor(paperWidthMm);
        var key = (ticket.Id, ticket.BillNumber, amount, width);
        var image = cache.Get(key);
        if (image is null)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Timeout);
            try
            {
                var business = await SettingsName(ct);
                var png = await daraja.DynamicQr(settings.Credentials!, settings.Live,
                    new DynamicQrRequest(business, ticket.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), (int)amount,
                        settings.Paybill, settings.PayTo), limit.Token).WaitAsync(Timeout, ct);
                image = images.FromPng(png, width);
                if (image is not null) cache.Put(key, image);
            }
            catch (Exception e) when (e is DarajaException or OperationCanceledException or TimeoutException or HttpRequestException && !ct.IsCancellationRequested)
            {
                log.LogInformation("Bill #{Ticket} printed without its M-Pesa QR: {Reason}", ticket.Number, e.Message);
            }
        }
        return new BillQr(image, settings.Paybill, settings.PayTo, amount, ticket.Number);
    }

    private async Task<string> SettingsName(CancellationToken ct)
    {
        var name = (await Settings.SettingsRegistry.Load(db, ct)).String(Settings.SettingsRegistry.BusinessName);
        return name.Length > 22 ? name[..22] : name;
    }
}
