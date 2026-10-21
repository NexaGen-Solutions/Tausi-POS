using Tausi.Domain.Common;

namespace Tausi.Domain.Service;

public enum ApprovalKind
{
    Void,
    Gift,
    Discount,
    PriceChange,
    Reprint,
    Move,
    Merge,
}

public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
    Expired,
}

/// <summary>
/// A request for an action the requester may only do with approval ("Option 2: supervisor
/// elsewhere"). Anyone whose role has the action Allowed can decide it, never the requester.
/// Approving runs the action on the server for the requester, in the same transaction.
/// </summary>
public class ApprovalRequest : VersionedEntity
{
    public const int MaxNoteLength = 200;

    public ApprovalKind Kind { get; set; }
    public Guid TicketId { get; set; }
    public Guid? OrderLineId { get; set; }

    /// <summary>What to do, as JSON: quantity, reason, amount, target table and so on.</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>For the approvals screen: "Void 1 × Guiness", and the amount it concerns.</summary>
    public string Summary { get; set; } = "";
    public decimal? Amount { get; set; }
    public string? Reason { get; set; }

    public Guid RequestedByUserId { get; set; }
    public Guid TerminalId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid? PreferredApproverId { get; set; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    /// <summary>The reject note, or why it expired.</summary>
    public string? DecisionNote { get; set; }

    public Guid WorkPeriodId { get; set; }

    public bool IsPending => Status == ApprovalStatus.Pending;
}
