using Tausi.Domain.Common;

namespace Tausi.Domain.Cash;

public enum ShiftStatus
{
    Open,
    Closed,
}

/// <summary>
/// A cashier's shift on one terminal's drawer, from the opening float to a counted, signed-off
/// cash-up. One open shift per cashier and one per terminal. Settling needs one.
/// </summary>
public class CashierShift : VersionedEntity
{
    public const int MaxNoteLength = 300;

    public Guid CashierUserId { get; set; }
    /// <summary>The terminal whose drawer it is.</summary>
    public Guid TerminalId { get; set; }
    public Guid WorkPeriodId { get; set; }

    public decimal OpeningFloat { get; set; }
    /// <summary>The float by denomination, when it was entered that way (JSON: {"1000":2,"coins":0}).</summary>
    public string? FloatCountsJson { get; set; }
    public DateTimeOffset OpenedAt { get; set; }

    public ShiftStatus Status { get; set; } = ShiftStatus.Open;

    /// <summary>The cashier's count by denomination (JSON), once submitted.</summary>
    public string? CountJson { get; set; }
    public decimal? Counted { get; set; }
    public DateTimeOffset? CountedAt { get; set; }
    /// <summary>Worked out when the count is submitted, and again at sign-off.</summary>
    public decimal? Expected { get; set; }
    /// <summary>Counted - expected: negative is short, positive is over.</summary>
    public decimal? Difference { get; set; }
    public string? Note { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? SignedOffByUserId { get; set; }

    public bool IsOpen => Status == ShiftStatus.Open;
}

public enum DrawerMovementKind
{
    /// <summary>Cash taken out of the drawer for an expense.</summary>
    PayOut,
    /// <summary>Cash moved from the drawer to the safe.</summary>
    Drop,
}

/// <summary>A pay-out or a cash drop during a shift. Audited, posted to the ledger, and a slip prints.</summary>
public class DrawerMovement : Entity
{
    public const int MaxNoteLength = 200;

    public Guid ShiftId { get; set; }
    public Guid WorkPeriodId { get; set; }
    public DrawerMovementKind Kind { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Pay-outs only.</summary>
    public Guid? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? Note { get; set; }
    public Guid ByUserId { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public Guid TerminalId { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>What a pay-out was for: Supplies, Transport, Casual labour, Repairs, Other. Task 12 adds the admin screen.</summary>
public class ExpenseCategory : VersionedEntity
{
    public const int MaxNameLength = 60;

    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;

    public static readonly IReadOnlyList<string> Defaults = ["Supplies", "Transport", "Casual labour", "Repairs", "Other"];
}
