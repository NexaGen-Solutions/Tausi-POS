using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Printing;

namespace Tausi.Application.Printers;

public sealed record TestPrintQueued(Guid PrintJobId);

/// <summary>
/// Queues a test slip (see <see cref="Slips.Test"/>): business, printer, connection and paper,
/// a column ruler, text styles, a sample bill and dispense slip, the date and "Test OK".
/// Queued like any job, so a failure shows in Failed jobs.
/// </summary>
public sealed class TestPrint(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    PrintQueue queue, ILogoProvider logos, IAuditLog audit, BusinessClock businessClock)
{
    public async Task<TestPrintQueued> Execute(Guid printerId, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.DevicesManage, approval.ApprovalToken, ct);
        var printer = await db.Printers.Include(p => p.OwningTerminal).FirstOrDefaultAsync(p => p.Id == printerId, ct)
            ?? throw new NotFoundException("Printer not found.");
        if (!printer.Active) throw new DomainException("This printer is not active.");
        var business = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);

        var logo = await logos.GetLogo(ReceiptBuilder.LogoWidthFor(printer.PaperWidthMm), ct);
        var settings = PrintSettings.From(SettingsRegistry.Read(business));
        var document = Slips.Test(
            new TestSlip(business?.Name ?? "", printer.Name, printer.ConnectionText(printer.OwningTerminal?.Name),
                printer.PaperWidthMm, business?.VatRate ?? 16m, await businessClock.Now(ct)),
            SlipStyle.For(printer, logo, business?.Currency ?? "KES", settings));

        var job = queue.Enqueue(printer, PrintJobKind.Test, document);
        audit.Write(new AuditRecord(
            AuditActions.TestPrint, "Printer", printer.Id.ToString(),
            NewValue: new { printJobId = job.Id }, ApprovedByUserId: grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        return new TestPrintQueued(job.Id);
    }
}
