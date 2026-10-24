using Tausi.Domain.Common;
using Tausi.Domain.Devices;

namespace Tausi.Domain.Printing;

public enum PrintJobKind
{
    Bill,
    Dispense,
    Void,
    Receipt,
    CashUp,
    Stock,
    Test,
}

public enum PrintJobStatus
{
    Queued,
    Sent,
    Failed,

    /// <summary>Given up on by an admin, with a reason.</summary>
    Discarded,
}

public class PrintJob : Entity
{
    /// <summary>The first try plus three retries.</summary>
    public const int MaxAttempts = 4;

    public Guid PrinterId { get; set; }
    public Printer? Printer { get; set; }
    public PrintJobKind Kind { get; set; }

    /// <summary>The ESC/POS bytes sent to the printer.</summary>
    public byte[] Content { get; set; } = [];

    /// <summary>The same slip as plain text, for the file transport and for showing on screen.</summary>
    public string TextPreview { get; set; } = "";

    public PrintJobStatus Status { get; set; } = PrintJobStatus.Queued;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public Guid? TicketId { get; set; }

    /// <summary>Why an admin discarded it.</summary>
    public string? DiscardReason { get; set; }

    /// <summary>
    /// Queues a failed job again. It keeps its place (its creation time), so a bill still prints
    /// before the dispense slip that came after it.
    /// </summary>
    public void Requeue(DateTimeOffset now)
    {
        Status = PrintJobStatus.Queued;
        Attempts = 0;
        NextAttemptAt = now;
    }

    /// <summary>Wait before the next try: 2 s, 4 s, 8 s.</summary>
    public static TimeSpan Backoff(int attemptsSoFar) => TimeSpan.FromSeconds(Math.Pow(2, attemptsSoFar));

    public void MarkSent(DateTimeOffset now)
    {
        Attempts++;
        Status = PrintJobStatus.Sent;
        SentAt = now;
        LastError = null;
    }

    /// <summary>Records a failed try. Returns true when the job has now failed for good.</summary>
    public bool MarkAttemptFailed(string error, DateTimeOffset now)
    {
        Attempts++;
        LastError = error;
        if (Attempts >= MaxAttempts)
        {
            Status = PrintJobStatus.Failed;
            return true;
        }
        NextAttemptAt = now + Backoff(Attempts);
        return false;
    }
}
