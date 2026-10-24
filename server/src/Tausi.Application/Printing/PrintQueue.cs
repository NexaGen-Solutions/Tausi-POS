using Tausi.Application.Abstractions;
using Tausi.Domain.Devices;
using Tausi.Domain.Printing;

namespace Tausi.Application.Printing;

/// <summary>
/// The only way code prints: it queues a <see cref="PrintJob"/> and the dispatcher sends it.
/// The job is saved with the caller's <c>SaveChangesAsync</c>, so a print failure never rolls back
/// the business action.
/// </summary>
public sealed class PrintQueue(ITausiDb db, TimeProvider clock, Security.ICurrentSession? session = null)
{
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>
    /// Jobs queued together keep their order: each gets a later time than the one before, so a bill
    /// queued before its dispense slip always prints first (ids made in the same millisecond don't
    /// sort in creation order).
    /// </summary>
    public PrintJob Enqueue(Printer printer, PrintJobKind kind, PrintDocument document, Guid? ticketId = null, string? label = null, bool reprint = false)
    {
        var now = clock.GetUtcNow();
        // A millisecond, not a tick: databases keep microseconds (PostgreSQL) or less.
        if (now <= _last) now = _last.AddMilliseconds(1);
        _last = now;
        var job = new PrintJob
        {
            PrinterId = printer.Id,
            Kind = kind,
            Content = document.EscPos,
            TextPreview = document.Text,
            CreatedAt = now,
            NextAttemptAt = now,
            TicketId = ticketId,
            CreatedByUserId = session?.User?.UserId,
            Label = label,
            IsReprint = reprint,
        };
        db.PrintJobs.Add(job);
        return job;
    }
}
