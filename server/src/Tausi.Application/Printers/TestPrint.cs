using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.People;
using Tausi.Domain.Printing;

namespace Tausi.Application.Printers;

public sealed record TestPrintQueued(Guid PrintJobId);

/// <summary>Queues a short test slip: business name, printer name, date and time, "Test OK".</summary>
public sealed class TestPrint(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
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
        // The footer shows here too when it is switched on, so the setting can be checked on paper.
        var document = PoweredByFooter
            .AddTo(ReceiptTemplate.Builder(Template, data, printer.Columns, logo), PoweredByFooter.IsOn(business?.OtherJson))
            .Feed(2)
            .Cut()
            .Build();

        var job = queue.Enqueue(printer, PrintJobKind.Test, document);
        audit.Write(new AuditRecord(
            AuditActions.TestPrint, "Printer", printer.Id.ToString(),
            NewValue: new { printJobId = job.Id }, ApprovedByUserId: grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        return new TestPrintQueued(job.Id);
    }
}
