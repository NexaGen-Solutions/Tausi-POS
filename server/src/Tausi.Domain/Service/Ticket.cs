using Tausi.Domain.Common;

namespace Tausi.Domain.Service;

public enum TicketStatus
{
    Open,
    Settled,
    Merged,
    Cancelled,
}

/// <summary>Who holds a ticket's edit lock.</summary>
public sealed record TicketLock(Guid TerminalId, Guid UserId, Guid SessionId, DateTimeOffset ExpiresAt);

/// <summary>
/// A bill for a table (or a takeaway customer). It is saved with its first line, never empty, and
/// gets its number then. Its total is always the sum of its lines; nothing stores a total.
/// While it is on screen on a terminal it carries an edit lock, renewed by that terminal.
/// </summary>
public class Ticket : VersionedEntity
{
    public const int MaxCustomerNameLength = 60;
    public const int MaxNoteLength = 1000;

    /// <summary>A lock not renewed for this long is gone. Terminals renew every 30 seconds.</summary>
    public static readonly TimeSpan LockLifetime = TimeSpan.FromSeconds(90);

    public long Number { get; set; }
    public Guid? TableId { get; set; }
    public DiningTable? Table { get; set; }
    public string? CustomerName { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public Guid OpenedByUserId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public Guid TerminalId { get; set; }
    public Guid WorkPeriodId { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset? BillPrintedAt { get; set; }
    public string Note { get; set; } = "";
    public Guid? MergedIntoTicketId { get; set; }

    public Guid? LockTerminalId { get; set; }
    public Guid? LockUserId { get; set; }
    public Guid? LockSessionId { get; set; }
    public DateTimeOffset? LockExpiresAt { get; set; }

    public List<OrderLine> Lines { get; set; } = [];

    public bool IsOpen => Status == TicketStatus.Open;

    public decimal Total => TotalOf(Lines);

    public static decimal TotalOf(IEnumerable<OrderLine> lines) => lines.Where(l => !l.IsVoided).Sum(l => l.Gross);

    /// <summary>The lock as stored, if it has not run out. Whether its session still lives is checked by the caller.</summary>
    public TicketLock? LockAt(DateTimeOffset now) =>
        LockTerminalId is { } terminal && LockUserId is { } user && LockSessionId is { } session
            && LockExpiresAt is { } expires && expires > now
            ? new TicketLock(terminal, user, session, expires)
            : null;

    public void TakeLock(Guid terminalId, Guid userId, Guid sessionId, DateTimeOffset now)
    {
        LockTerminalId = terminalId;
        LockUserId = userId;
        LockSessionId = sessionId;
        LockExpiresAt = now + LockLifetime;
    }

    public void ClearLock()
    {
        LockTerminalId = null;
        LockUserId = null;
        LockSessionId = null;
        LockExpiresAt = null;
    }

    public void Touch(DateTimeOffset now) => LastActivityAt = now;

    /// <summary>
    /// Moves the other tickets' lines here unchanged (who added them, terminal, time, printed state
    /// and price stay as they were), marks those tickets Merged and joins the notes.
    /// </summary>
    public void MergeIn(IReadOnlyList<Ticket> others, DateTimeOffset now)
    {
        if (!IsOpen) throw new DomainException($"#{Number} is not open.");
        foreach (var other in others)
        {
            if (other.Id == Id) throw new DomainException("A ticket can't be merged into itself.");
            if (!other.IsOpen) throw new DomainException($"#{other.Number} is not open.");
            if (other.TableId != TableId) throw new DomainException("Only tickets on the same table can be merged.");
        }

        foreach (var other in others)
        {
            foreach (var line in other.Lines.ToList())
            {
                other.Lines.Remove(line);
                line.TicketId = Id;
                Lines.Add(line);
            }
            other.Status = TicketStatus.Merged;
            other.MergedIntoTicketId = Id;
            other.ClearLock();
            other.Touch(now);
        }

        var notes = new[] { this }.Concat(others).Select(t => t.Note.Trim()).Where(n => n.Length > 0);
        Note = string.Join(" / ", notes);
        if (Note.Length > MaxNoteLength) Note = Note[..MaxNoteLength];
        Touch(now);
    }
}
