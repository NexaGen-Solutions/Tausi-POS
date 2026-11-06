using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Tausi.Relay;

/// <summary>Wakes a business's waiting collection when one of its payments arrives.</summary>
public sealed class Notifier
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _waits = new();

    public Task Wait(Guid business) =>
        _waits.GetOrAdd(business, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void Signal(Guid business)
    {
        if (_waits.TryRemove(business, out var wait)) wait.TrySetResult();
    }
}

/// <summary>Saves callbacks: one row per business and transaction id, however often Safaricom repeats it.</summary>
public sealed class Store(RelayDb db, Notifier notifier, TimeProvider clock, ILogger<Store> log)
{
    public async Task<bool> Save(RelayPayment payment, CancellationToken ct)
    {
        if (await db.Payments.AnyAsync(p => p.BusinessId == payment.BusinessId && p.Key == payment.Key, ct)) return false;
        payment.ReceivedAt = clock.GetUtcNow().UtcDateTime;
        db.Payments.Add(payment);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same callback at the same moment: the unique index kept the first.
            db.ChangeTracker.Clear();
            return false;
        }
        log.LogInformation("Stored {Kind} {Key} for business {Business}", payment.Kind, payment.Key, payment.BusinessId);
        notifier.Signal(payment.BusinessId);
        return true;
    }
}

/// <summary>A payment as a bar's server receives it.</summary>
public sealed record DeliveredPayment(
    Guid Id, string Kind, string? TransactionId, string? CheckoutRequestId, string? MerchantRequestId, int? ResultCode, string? ResultDesc,
    decimal Amount, string? PayerName, string? Phone, string? ShortCode, string? BillRef, string? TransactionType, DateTime? PaidAt,
    DateTime ReceivedAt);

public sealed record AckRequest(IReadOnlyList<Guid> Ids);

/// <summary>
/// A bar's server collects its payments with its API key (Bearer): every payment it hasn't
/// acknowledged, at once, or (long-poll) the next ones as they arrive, waiting up to 25 seconds.
/// It acknowledges each after saving it. A business only ever sees its own payments.
/// </summary>
public static class Delivery
{
    public const int MaxBatch = 200;
    public const int MaxWaitSeconds = 25;

    public static void MapDelivery(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapGet("/payments", async (int? wait, HttpContext http, RelayDb db, Notifier notifier, TimeProvider clock, CancellationToken ct) =>
        {
            if (await Authenticate(http, db, ct) is not { } business) return Results.Unauthorized();
            var waited = Math.Clamp(wait ?? 0, 0, MaxWaitSeconds);
            var signal = notifier.Wait(business.Id);
            var list = await Pending(db, business.Id, ct);
            if (list.Count == 0 && waited > 0)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(waited));
                try
                {
                    await signal.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                }
                list = await Pending(db, business.Id, ct);
            }
            await db.Businesses.Where(b => b.Id == business.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.LastSeenAt, clock.GetUtcNow().UtcDateTime), ct);
            return Results.Ok(list);
        });
        api.MapPost("/payments/ack", async (AckRequest request, HttpContext http, RelayDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (await Authenticate(http, db, ct) is not { } business) return Results.Unauthorized();
            var ids = (request.Ids ?? []).Distinct().Take(MaxBatch).ToList();
            var now = clock.GetUtcNow().UtcDateTime;
            var count = await db.Payments.Where(p => p.BusinessId == business.Id && ids.Contains(p.Id) && p.AcknowledgedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.AcknowledgedAt, now), ct);
            return Results.Ok(new { acknowledged = count });
        });
        // The bar's Test connection: the key works.
        api.MapGet("/me", async (HttpContext http, RelayDb db, IOptions<RelayOptions> options, CancellationToken ct) =>
            await Authenticate(http, db, ct) is { } business
                ? Results.Ok(new { business.Name, Callbacks = CallbackUrls(options.Value.PublicUrl, business.PathToken) })
                : Results.Unauthorized());
    }

    public static async Task<Business?> Authenticate(HttpContext http, RelayDb db, CancellationToken ct)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var key = header["Bearer ".Length..].Trim();
        if (!key.StartsWith(RelaySecrets.KeyPrefix, StringComparison.Ordinal)) return null;
        var hash = RelaySecrets.Hash(key);
        var business = await db.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.ApiKeyHash == hash && b.Active, ct);
        return business is not null && RelaySecrets.SameHash(business.ApiKeyHash, hash) ? business : null;
    }

    private static Task<List<DeliveredPayment>> Pending(RelayDb db, Guid business, CancellationToken ct) =>
        db.Payments.AsNoTracking().Where(p => p.BusinessId == business && p.AcknowledgedAt == null)
            .OrderBy(p => p.ReceivedAt).Take(MaxBatch)
            .Select(p => new DeliveredPayment(p.Id, p.Kind, p.TransactionId, p.CheckoutRequestId, p.MerchantRequestId, p.ResultCode,
                p.ResultDesc, p.Amount, p.PayerName, p.Phone, p.ShortCode, p.BillRef, p.TransactionType, p.PaidAt, p.ReceivedAt))
            .ToListAsync(ct);

    /// <summary>The three addresses to register with Daraja for a business.</summary>
    public static object CallbackUrls(string publicUrl, string token)
    {
        var root = publicUrl.TrimEnd('/') + "/pay/" + token;
        return new { Stk = root + "/stk", Validation = root + "/c2b/validation", Confirmation = root + "/c2b/confirmation" };
    }
}

/// <summary>Deletes payments acknowledged more than <see cref="RelayOptions.KeepDays"/> days ago, every hour.</summary>
public sealed class Cleanup(IServiceScopeFactory scopes, IOptions<RelayOptions> options, TimeProvider clock, ILogger<Cleanup> log) : BackgroundService
{
    public static Task<int> Run(RelayDb db, DateTime now, int keepDays, CancellationToken ct)
    {
        var before = now.AddDays(-keepDays);
        return db.Payments.Where(p => p.AcknowledgedAt != null && p.AcknowledgedAt < before).ExecuteDeleteAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var deleted = await Run(scope.ServiceProvider.GetRequiredService<RelayDb>(), clock.GetUtcNow().UtcDateTime, options.Value.KeepDays, stoppingToken);
                if (deleted > 0) log.LogInformation("Deleted {Count} payments acknowledged over {Days} days ago", deleted, options.Value.KeepDays);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Clean-up failed");
            }
            await Task.Delay(TimeSpan.FromHours(1), clock, stoppingToken);
        }
    }
}
