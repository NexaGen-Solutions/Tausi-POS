using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Tausi.Application.Abstractions;
using Tausi.Application.Mpesa;
using Tausi.Application.Printing;
using Tausi.Domain.Service;

namespace Tausi.Infrastructure.Mpesa;

/// <summary>The relay's delivery API (relay/src/Tausi.Relay/Delivery.cs), with the business's API key.</summary>
public sealed class HttpRelayClient(HttpClient? http = null) : IRelayClient
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(40) };
    private readonly HttpClient Http = http ?? Shared;

    public async Task<IReadOnlyList<RelayPaymentDto>> Collect(string relayUrl, string apiKey, int waitSeconds, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"{relayUrl}/api/v1/payments?wait={waitSeconds}", apiKey);
        using var response = await Http.SendAsync(request, ct);
        Check(response);
        return await response.Content.ReadFromJsonAsync<List<RelayPaymentDto>>(ct) ?? [];
    }

    public async Task Acknowledge(string relayUrl, string apiKey, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"{relayUrl}/api/v1/payments/ack", apiKey);
        request.Content = JsonContent.Create(new { ids });
        using var response = await Http.SendAsync(request, ct);
        Check(response);
    }

    public async Task<RelayAccount> Me(string relayUrl, string apiKey, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"{relayUrl}/api/v1/me", apiKey);
        using var response = await Http.SendAsync(request, ct);
        Check(response);
        return await response.Content.ReadFromJsonAsync<RelayAccount>(ct) ?? throw new InvalidOperationException("The relay gave no answer.");
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("The relay didn't accept the API key.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The relay answered {(int)response.StatusCode}.");
    }
}

/// <summary>
/// Collects payments from the relay and saves them (<see cref="MpesaInbox"/>), acknowledging each
/// once saved. It stays connected (long-poll, 25 seconds at a time) while any ticket is open in an
/// open work period, and otherwise checks every 10 seconds whether one has opened. Nothing is lost
/// while it is off: the relay holds payments until they are acknowledged. Also the catch-up
/// collection (<see cref="IMpesaCatchUp"/>) for day open, Close day and Fetch payments now.
/// </summary>
public sealed class RelayCollector(IServiceScopeFactory scopes, IRelayClient relay, RelayStatus status, TimeProvider clock, ILogger<RelayCollector> log)
    : BackgroundService, IMpesaCatchUp
{
    public const int WaitSeconds = 25;
    public static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AfterError = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _one = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan pause;
            try
            {
                pause = await Once(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                status.Set(RelayState.Unreachable, e.Message);
                log.LogWarning("M-Pesa relay: {Message}", e.Message);
                pause = AfterError;
            }
            if (pause > TimeSpan.Zero) await Task.Delay(pause, clock, stoppingToken);
        }
    }

    /// <summary>One turn of the loop: how long to wait before the next.</summary>
    public async Task<TimeSpan> Once(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ITausiDb>();
        var settings = await MpesaSettings.Load(db, scope.ServiceProvider.GetRequiredService<ISecretProtector>(), ct);
        if (!settings.RelaySetUp)
        {
            status.Set(RelayState.NotSetUp);
            return IdleCheck;
        }
        if (!await TicketsOpen(db, ct))
        {
            status.Set(RelayState.Idle, "no open tickets");
            return IdleCheck;
        }
        status.Set(RelayState.Connected);
        await Collect(settings, WaitSeconds, ct);
        return TimeSpan.Zero;
    }

    public async Task<int> Run(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var settings = await MpesaSettings.Load(scope.ServiceProvider.GetRequiredService<ITausiDb>(),
            scope.ServiceProvider.GetRequiredService<ISecretProtector>(), ct);
        if (!settings.RelaySetUp) return 0;
        var total = 0;
        for (var i = 0; i < 10; i++)
        {
            var saved = await Collect(settings, 0, ct);
            total += saved;
            if (saved < 200) break;
        }
        return total;
    }

    public static Task<bool> TicketsOpen(ITausiDb db, CancellationToken ct) =>
        db.Tickets.AnyAsync(k => k.Status == TicketStatus.Open && db.WorkPeriods.Any(w => w.Id == k.WorkPeriodId && w.IsOpen), ct);

    private async Task<int> Collect(MpesaSettings settings, int wait, CancellationToken ct)
    {
        var payments = await relay.Collect(settings.RelayUrl, settings.RelayApiKey!, wait, ct);
        if (payments.Count == 0)
        {
            status.Collected(clock.GetUtcNow(), 0);
            return 0;
        }
        // One at a time: the background loop and a catch-up may run together; saving is idempotent anyway.
        await _one.WaitAsync(ct);
        var saved = new List<Guid>();
        try
        {
            foreach (var payment in payments)
            {
                await using var scope = scopes.CreateAsyncScope();
                try
                {
                    if (await scope.ServiceProvider.GetRequiredService<MpesaInbox>().Receive(payment, ct)) saved.Add(payment.Id);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Couldn't save M-Pesa payment {Id} from the relay; it stays on the relay for next time", payment.Id);
                }
            }
        }
        finally
        {
            _one.Release();
        }
        if (saved.Count > 0) await relay.Acknowledge(settings.RelayUrl, settings.RelayApiKey!, saved, ct);
        status.Collected(clock.GetUtcNow(), saved.Count);
        return saved.Count;
    }
}

/// <summary>Daraja's QR PNG as a 1-bit raster: scaled with whole pixels (nearest), so the modules stay sharp.</summary>
public sealed class SkiaMonoImages : IMonoImages
{
    public MonoBitmap? FromPng(byte[] png, int widthDots)
    {
        using var source = SKBitmap.Decode(png);
        if (source is null) return null;
        using var resized = source.Resize(new SKImageInfo(widthDots, Math.Max(1, source.Height * widthDots / source.Width)),
            new SKSamplingOptions(SKFilterMode.Nearest)) ?? source;
        var (width, height) = (resized.Width, resized.Height);
        var bytesPerRow = (width + 7) / 8;
        var data = new byte[bytesPerRow * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var c = resized.GetPixel(x, y);
            var luminance = (0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue) * c.Alpha / 255 + 255 - c.Alpha;
            if (luminance < 128) data[y * bytesPerRow + x / 8] |= (byte)(0x80 >> (x % 8));
        }
        return new MonoBitmap(width, height, data);
    }
}
