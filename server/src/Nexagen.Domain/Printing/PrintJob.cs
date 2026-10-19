using Nexagen.Domain.Common;
using Nexagen.Domain.Devices;

namespace Nexagen.Domain.Printing;

public enum PrintJobKind
{
    Bill,
    Dispense,
    Void,
    Receipt,
    CashUp,
    Test,
}

public enum PrintJobStatus
{
    Queued,
    Sent,
    Failed,
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
