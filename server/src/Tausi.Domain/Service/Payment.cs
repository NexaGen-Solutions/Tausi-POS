using System.Text.RegularExpressions;
using Tausi.Domain.Common;

namespace Tausi.Domain.Service;

public enum PaymentType
{
    Cash,
    Mpesa,
    Card,
}

/// <summary>Pending: added while settling, can be removed. Final: the ticket was settled. Removed: taken off before settling.</summary>
public enum PaymentStatus
{
    Pending,
    Final,
    Removed,
}

/// <summary>
/// One payment towards a ticket, saved as the cashier adds it (a crash loses nothing). Taken by
/// the cashier (from the session), brought by the waiter (or whoever brought the money).
/// </summary>
public partial class Payment : Entity
{
    public const int MpesaCodeLength = 10;
    public const int MaxReferenceLength = 40;

    public Guid TicketId { get; set; }
    public PaymentType Type { get; set; }
    /// <summary>What goes towards the ticket.</summary>
    public decimal Amount { get; set; }
    /// <summary>Cash handed over (cash only); Amount = min(tendered, remaining).</summary>
    public decimal? Tendered { get; set; }
    /// <summary>Tendered - Amount (cash only).</summary>
    public decimal? Change { get; set; }
    /// <summary>Upper case, 10 letters or digits; unique across all payments ever.</summary>
    public string? MpesaCode { get; set; }
    public string? CardReference { get; set; }
    public Guid TakenByUserId { get; set; }
    public Guid BroughtByUserId { get; set; }
    public Guid TerminalId { get; set; }
    public Guid WorkPeriodId { get; set; }
    public DateTimeOffset TakenAt { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public DateTimeOffset? RemovedAt { get; set; }
    public Guid? RemovedByUserId { get; set; }

    public bool Counts => Status != PaymentStatus.Removed;

    /// <summary>"SJK3abc123" → "SJK3ABC123", or null when it isn't 10 letters or digits.</summary>
    public static string? CleanMpesaCode(string? code)
    {
        var clean = (code ?? "").Trim().ToUpperInvariant();
        return MpesaCodeShape().IsMatch(clean) ? clean : null;
    }

    [GeneratedRegex("^[A-Z0-9]{10}$")]
    private static partial Regex MpesaCodeShape();
}
