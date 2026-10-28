using Tausi.Domain.Common;

namespace Tausi.Domain.Service;

/// <summary>
/// A business day ("work period"). Only one may be open at a time; the database enforces it
/// with a unique index on <see cref="IsOpen"/> for open rows. Closing comes with cash-up.
/// </summary>
public class WorkPeriod : VersionedEntity, ITrainingData
{
    /// <inheritdoc />
    public bool IsTraining { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>The day's date: when it was opened, in the business's time zone. Its entries carry this date.</summary>
    public DateOnly? BusinessDate { get; set; }
    public Guid OpenedByUserId { get; set; }
    public Guid? OpenedTerminalId { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public Guid? ClosedTerminalId { get; set; }

    /// <summary>When the POS sessions still open at the close were logged out (60 seconds after it).</summary>
    public DateTimeOffset? SessionsEndedAt { get; set; }

    /// <summary>True until the day is closed. Kept as a column so "only one open" can be a unique index.</summary>
    public bool IsOpen { get; set; } = true;
}
