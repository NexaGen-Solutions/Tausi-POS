using Nexagen.Domain.Common;

namespace Nexagen.Domain.Service;

/// <summary>
/// A business day ("work period"). Only one may be open at a time; the database enforces it
/// with a unique index on <see cref="IsOpen"/> for open rows. Closing comes with cash-up.
/// </summary>
public class WorkPeriod : VersionedEntity
{
    public DateTimeOffset OpenedAt { get; set; }
    public Guid OpenedByUserId { get; set; }
    public Guid? OpenedTerminalId { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public Guid? ClosedTerminalId { get; set; }

    /// <summary>True until the day is closed. Kept as a column so "only one open" can be a unique index.</summary>
    public bool IsOpen { get; set; } = true;
}
