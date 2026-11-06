namespace Tausi.Application.Mpesa;

/// <summary>A payment as the relay delivers it (see relay/src/Tausi.Relay/Delivery.cs).</summary>
public sealed record RelayPaymentDto(
    Guid Id, string Kind, string? TransactionId, string? CheckoutRequestId, string? MerchantRequestId, int? ResultCode, string? ResultDesc,
    decimal Amount, string? PayerName, string? Phone, string? ShortCode, string? BillRef, string? TransactionType, DateTime? PaidAt,
    DateTime ReceivedAt);

/// <summary>The relay's answer to Test connection.</summary>
public sealed record RelayAccount(string Name, RelayCallbacks Callbacks);

public sealed record RelayCallbacks(string Stk, string Validation, string Confirmation);

/// <summary>The bar's side of the relay: collect (waiting up to <c>waitSeconds</c> for new ones) and acknowledge.</summary>
public interface IRelayClient
{
    Task<IReadOnlyList<RelayPaymentDto>> Collect(string relayUrl, string apiKey, int waitSeconds, CancellationToken ct);

    Task Acknowledge(string relayUrl, string apiKey, IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<RelayAccount> Me(string relayUrl, string apiKey, CancellationToken ct);
}

public enum RelayState
{
    NotSetUp,
    Idle,
    Connected,
    Unreachable,
}

/// <summary>How the connection to the relay is doing (board 26), kept in memory by the collector.</summary>
public sealed class RelayStatus
{
    private readonly Lock _gate = new();

    public RelayState State { get; private set; } = RelayState.NotSetUp;
    public string? Reason { get; private set; }
    public DateTimeOffset? LastMessageAt { get; private set; }
    public DateTimeOffset? LastCollectedAt { get; private set; }

    public void Set(RelayState state, string? reason = null)
    {
        lock (_gate)
        {
            State = state;
            Reason = reason;
        }
    }

    public void Collected(DateTimeOffset at, int count)
    {
        lock (_gate)
        {
            LastCollectedAt = at;
            if (count > 0) LastMessageAt = at;
        }
    }
}

/// <summary>
/// Collecting from the relay outside the background loop: when the day opens, before Close day, and
/// on "Fetch payments now". Returns how many payments were saved, or throws when the relay can't be reached.
/// </summary>
public interface IMpesaCatchUp
{
    Task<int> Run(CancellationToken ct);
}
