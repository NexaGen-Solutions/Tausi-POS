using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Auditing;
using Nexagen.Application.Common;
using Nexagen.Application.Printing;
using Nexagen.Application.Security;
using Nexagen.Domain.People;
using Nexagen.Domain.Printing;

namespace Nexagen.Application.Printers;

public sealed record TestPrintQueued(Guid PrintJobId);

/// <summary>Queues a short test slip: business name, printer name, date and time, "Test OK".</summary>
public sealed class TestPrint(
    INexagenDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    PrintQueue queue, ILogoProvider logos, IAuditLog audit, TimeProvider clock)
{
    public const string Template = """
        @logo
        [center][bold][big]{{business}}
        [center]Printer test
        ---
        Printer || {{printer}}
        Connection || {{connection}}
        Paper || {{paper}} mm
        Date || {{date}}
        Time || {{time}}
        ---
        [center][bold][big]Test OK
        @feed 2
        @cut
        """;

    public async Task<TestPrintQueued> Execute(Guid printerId, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.DevicesManage, approval.ApprovalToken, ct);
        var printer = await db.Printers.FirstOrDefaultAsync(p => p.Id == printerId, ct)
            ?? throw new NotFoundException("Printer not found.");
        var business = await db.BusinessSettings.AsNoTracking().FirstOrDefaultAsync(ct);

        var now = clock.GetLocalNow();
        var data = new Dictionary<string, string>
        {
            ["business"] = business?.Name ?? "",
            ["printer"] = printer.Name,
            ["connection"] = printer.ConnectionMode.ToString(),
            ["paper"] = printer.PaperWidthMm.ToString(CultureInfo.InvariantCulture),
            ["date"] = now.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            ["time"] = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        };
        var logo = await logos.GetLogo(ReceiptBuilder.LogoWidthFor(printer.PaperWidthMm), ct);
        var document = ReceiptTemplate.Render(Template, data, printer.Columns, logo);

        var job = queue.Enqueue(printer, PrintJobKind.Test, document);
        audit.Write(new AuditRecord(
            AuditActions.TestPrint, "Printer", printer.Id.ToString(),
            NewValue: new { printJobId = job.Id }, ApprovedByUserId: grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        return new TestPrintQueued(job.Id);
    }
}
