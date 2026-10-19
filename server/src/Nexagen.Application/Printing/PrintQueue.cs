using Nexagen.Application.Abstractions;
using Nexagen.Domain.Devices;
using Nexagen.Domain.Printing;

namespace Nexagen.Application.Printing;

/// <summary>
/// The only way code prints: it queues a <see cref="PrintJob"/> and the dispatcher sends it.
/// The job is saved with the caller's <c>SaveChangesAsync</c>, so a print failure never rolls back
/// the business action.
/// </summary>
public sealed class PrintQueue(INexagenDb db, TimeProvider clock)
{
    public PrintJob Enqueue(Printer printer, PrintJobKind kind, PrintDocument document, Guid? ticketId = null)
    {
        var now = clock.GetUtcNow();
        var job = new PrintJob
        {
            PrinterId = printer.Id,
            Kind = kind,
            Content = document.EscPos,
            TextPreview = document.Text,
            CreatedAt = now,
            NextAttemptAt = now,
            TicketId = ticketId,
        };
        db.PrintJobs.Add(job);
        return job;
    }
}
