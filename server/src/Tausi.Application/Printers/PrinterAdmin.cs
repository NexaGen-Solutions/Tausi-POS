using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Devices;
using Tausi.Domain.People;
using Tausi.Domain.Printing;

namespace Tausi.Application.Printers;

public sealed record PrinterUse(string Terminal, IReadOnlyList<string> Roles);

public sealed record PrinterRow(
    Guid Id, string Name, PrinterConnection ConnectionMode, string Address, Guid? OwningTerminalId, string? OwningTerminalName,
    string ConnectionText, int PaperWidthMm, bool CutAfterSlip, bool OpenDrawerOnReceipt, bool Active,
    PrinterStatus Status, string? LastError, DateTimeOffset? LastPrintedAt, int FailedJobs,
    IReadOnlyList<PrinterUse> UsedBy, string? DispenseTemplate, long RowVersion);

public sealed record PrinterRequest(
    string Name, PrinterConnection ConnectionMode, string? Address, Guid? OwningTerminalId, int PaperWidthMm,
    bool CutAfterSlip, bool OpenDrawerOnReceipt, bool Active, long RowVersion = 0);

public sealed record CheckPrinterRequest(PrinterConnection ConnectionMode, string? Address, Guid? OwningTerminalId);

public sealed record CheckResult(bool Reachable, string Message);

public sealed record PrintJobRow(Guid Id, DateTimeOffset CreatedAt, PrintJobKind Kind, Guid? TicketId, string? Error, int Attempts, PrintJobStatus Status, string PrinterName);

public sealed record RedirectRequest(Guid PrinterId);

public sealed record DiscardRequest(string Reason);

public static class PrinterCodes
{
    public const string AddressRequired = "address_required";
    public const string InUse = "printer_in_use";
    public const string DevelopmentOnly = "development_only";
}

/// <summary>Shared by the printer use cases (devices.manage).</summary>
public sealed class PrinterAdmin(ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval, DevelopmentHost? host = null)
{
    public Task<PermissionGrant> Require(CancellationToken ct) =>
        permissions.Require(session.RequireUser(), PermissionKeys.DevicesManage, approval.ApprovalToken, ct);

    public async Task<IReadOnlyList<PrinterRow>> Rows(Guid? onlyId, CancellationToken ct)
    {
        var printers = await db.Printers.AsNoTracking().Include(p => p.OwningTerminal)
            .Where(p => onlyId == null || p.Id == onlyId).OrderBy(p => p.Name).ToListAsync(ct);
        var terminals = await db.Terminals.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var failed = await db.PrintJobs.AsNoTracking().Where(j => j.Status == PrintJobStatus.Failed)
            .GroupBy(j => j.PrinterId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return printers.Select(p => new PrinterRow(
            p.Id, p.Name, p.ConnectionMode, p.Address, p.OwningTerminalId, p.OwningTerminal?.Name,
            p.ConnectionText(p.OwningTerminal?.Name), p.PaperWidthMm, p.CutAfterSlip, p.OpenDrawerOnReceipt, p.Active,
            p.Status, p.LastError, p.LastPrintedAt, failed.GetValueOrDefault(p.Id),
            UsedBy(p.Id, terminals), p.DispenseTemplate, p.RowVersion)).ToList();
    }

    public static IReadOnlyList<PrinterUse> UsedBy(Guid printerId, IEnumerable<Terminal> terminals) =>
        terminals
            .Select(t => new PrinterUse(t.Name, new[]
            {
                t.DispensePrinterId == printerId ? $"Dispense \"{t.EffectiveDispenseHeader}\"" : null,
                t.ReceiptPrinterId == printerId ? "Bills and receipts" : null,
            }.OfType<string>().ToList()))
            .Where(u => u.Roles.Count > 0)
            .ToList();

    /// <summary>Checks the address for the connection mode, and returns it tidied ("host:9100").</summary>
    public async Task<string> ValidAddress(PrinterConnection mode, string? address, Guid? owningTerminalId, CancellationToken ct)
    {
        var a = (address ?? "").Trim();
        switch (mode)
        {
            case PrinterConnection.Network9100:
            {
                var colon = a.LastIndexOf(':');
                var host = colon > 0 ? a[..colon] : a;
                var port = 9100;
                if (host.Length == 0 || host.Contains(' ')
                    || (colon > 0 && (!int.TryParse(a[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)))
                    throw new DomainException("Enter the printer's IP address, and the port if it isn't 9100 (e.g. 192.168.1.50:9100).", PrinterCodes.AddressRequired);
                return $"{host}:{port}";
            }
            case PrinterConnection.WindowsShare:
                if (!a.StartsWith(@"\\", StringComparison.Ordinal) || a.Length < 5 || a[2..].IndexOf('\\') < 1 || a.EndsWith('\\'))
                    throw new DomainException(@"Enter the share name like \\COUNTER-A\TicketPrinter1.", PrinterCodes.AddressRequired);
                return a;
            case PrinterConnection.ViaTerminal:
                if (owningTerminalId is not { } tid || !await db.Terminals.AnyAsync(t => t.Id == tid, ct))
                    throw new DomainException("Choose the terminal the printer is plugged into.", PrinterCodes.AddressRequired);
                if (a.Length == 0)
                    throw new DomainException("Choose the printer's name on that terminal.", PrinterCodes.AddressRequired);
                return a;
            case PrinterConnection.File:
                // Writes slips to print-output/ for development; an installed server has real printers only.
                if (host?.IsDevelopment != true)
                    throw new DomainException("The File printer is for development only. Choose Network, Windows share or Via terminal.", PrinterCodes.DevelopmentOnly);
                return "";
            default:
                return "";
        }
    }
}

public sealed class ListPrinters(PrinterAdmin admin)
{
    public async Task<IReadOnlyList<PrinterRow>> Execute(CancellationToken ct)
    {
        await admin.Require(ct);
        return await admin.Rows(null, ct);
    }
}

/// <summary>Adds or edits a printer. A printer a terminal uses can't be deactivated until it is replaced there.</summary>
public sealed class SavePrinter(ITausiDb db, PrinterAdmin admin, IAuditLog audit, IPosEvents events)
{
    public async Task<PrinterRow> Execute(Guid? id, PrinterRequest request, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        var name = (request.Name ?? "").Trim();
        if (name.Length is < 2 or > 40) throw new DomainException("The printer name must be 2 to 40 characters.");
        var upper = name.ToUpperInvariant();
        if (await db.Printers.AnyAsync(p => p.Id != id && p.Name.ToUpper() == upper, ct))
            throw new DomainException($"There is already a printer called {name}.");
        if (request.PaperWidthMm is not (58 or 80)) throw new DomainException("Paper is 58 or 80 mm.");
        var address = await admin.ValidAddress(request.ConnectionMode, request.Address, request.OwningTerminalId, ct);

        Printer printer;
        object? before = null;
        if (id is { } existing)
        {
            printer = await db.Printers.FirstOrDefaultAsync(p => p.Id == existing, ct) ?? throw new NotFoundException("Printer not found.");
            if (printer.RowVersion != request.RowVersion)
                throw new ConflictException("This printer was changed by someone else. Reload and try again.", "stale");
            ((DbContext)db).Entry(printer).Property(p => p.RowVersion).OriginalValue = request.RowVersion;
            if (printer.Active && !request.Active)
            {
                var users = PrinterAdmin.UsedBy(printer.Id, await db.Terminals.AsNoTracking().ToListAsync(ct));
                if (users.Count > 0)
                    throw new DomainException(
                        $"{printer.Name} is used by {string.Join(", ", users.Select(u => u.Terminal))}. Choose another printer there first.",
                        PrinterCodes.InUse);
            }
            before = View(printer);
        }
        else
        {
            printer = new Printer();
            db.Printers.Add(printer);
        }
        var settingsChanged = printer.ConnectionMode != request.ConnectionMode || printer.Address != address;
        printer.Name = name;
        printer.ConnectionMode = request.ConnectionMode;
        printer.Address = address;
        printer.OwningTerminalId = request.ConnectionMode == PrinterConnection.ViaTerminal ? request.OwningTerminalId : null;
        printer.PaperWidthMm = request.PaperWidthMm;
        printer.CutAfterSlip = request.CutAfterSlip;
        printer.OpenDrawerOnReceipt = request.OpenDrawerOnReceipt;
        printer.Active = request.Active;
        if (settingsChanged)
        {
            printer.Status = PrinterStatus.Unknown;
            printer.LastError = null;
        }

        audit.Write(new AuditRecord("printer.changed", "Printer", printer.Id.ToString(), before, View(printer), grant.ApprovedByUserId));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("This printer was changed by someone else. Reload and try again.", "stale");
        }
        await events.Publish(PosEventNames.PrinterStatus, new { printerId = printer.Id, status = printer.Status.ToString() }, ct);
        return (await admin.Rows(printer.Id, ct))[0];
    }

    private static object View(Printer p) => new
    {
        p.Name, mode = p.ConnectionMode.ToString(), p.Address, p.OwningTerminalId, p.PaperWidthMm, p.CutAfterSlip, p.OpenDrawerOnReceipt, p.Active,
    };
}

/// <summary>"Check connection" with the values on the form (saved or not). Answers in plain words.</summary>
public sealed class CheckPrinter(PrinterAdmin admin, IPrinterChecker checker)
{
    public async Task<CheckResult> Execute(CheckPrinterRequest request, CancellationToken ct)
    {
        await admin.Require(ct);
        string address;
        try
        {
            address = await admin.ValidAddress(request.ConnectionMode, request.Address, request.OwningTerminalId, ct);
        }
        catch (DomainException e)
        {
            return new CheckResult(false, e.Message);
        }
        try
        {
            await checker.Check(request.ConnectionMode, address, request.OwningTerminalId, ct);
            return new CheckResult(true, "Reachable");
        }
        catch (PrinterTransportException e)
        {
            return new CheckResult(false, e.Message);
        }
    }
}

/// <summary>A printer's jobs, newest first: failed ones by default (the Failed jobs panel).</summary>
public sealed class ListPrinterJobs(ITausiDb db, PrinterAdmin admin)
{
    public async Task<IReadOnlyList<PrintJobRow>> Execute(Guid printerId, PrintJobStatus status, CancellationToken ct)
    {
        await admin.Require(ct);
        return await db.PrintJobs.AsNoTracking().Include(j => j.Printer)
            .Where(j => j.PrinterId == printerId && j.Status == status)
            .OrderByDescending(j => j.CreatedAt).Take(200)
            .Select(j => new PrintJobRow(j.Id, j.CreatedAt, j.Kind, j.TicketId, j.LastError, j.Attempts, j.Status, j.Printer!.Name))
            .ToListAsync(ct);
    }
}

/// <summary>
/// Failed-job actions: retry (keeps its place in the queue), send to another printer, discard (with
/// a reason). All audited.
/// </summary>
public sealed class PrintJobActions(ITausiDb db, PrinterAdmin admin, IAuditLog audit, TimeProvider clock)
{
    public async Task Retry(Guid jobId, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        var job = await Failed(jobId, ct);
        job.Requeue(clock.GetUtcNow());
        audit.Write(new AuditRecord("print.job_retried", "PrintJob", job.Id.ToString(), null, new { job.Kind }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> RetryAll(Guid printerId, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        var jobs = await db.PrintJobs.Where(j => j.PrinterId == printerId && j.Status == PrintJobStatus.Failed).ToListAsync(ct);
        foreach (var job in jobs) job.Requeue(clock.GetUtcNow());
        audit.Write(new AuditRecord("print.jobs_retried", "Printer", printerId.ToString(), null, new { count = jobs.Count }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        return jobs.Count;
    }

    public async Task Redirect(Guid jobId, RedirectRequest request, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        var job = await Failed(jobId, ct);
        var target = await db.Printers.FirstOrDefaultAsync(p => p.Id == request.PrinterId && p.Active, ct)
            ?? throw new DomainException("Choose an active printer.");
        var from = job.PrinterId;
        job.PrinterId = target.Id;
        job.Requeue(clock.GetUtcNow());
        audit.Write(new AuditRecord("print.job_redirected", "PrintJob", job.Id.ToString(),
            new { printerId = from }, new { printerId = target.Id, printer = target.Name }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
    }

    public async Task Discard(Guid jobId, DiscardRequest request, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        var reason = (request.Reason ?? "").Trim();
        if (reason.Length < 3) throw new DomainException("Say why this slip is being discarded.");
        var job = await Failed(jobId, ct);
        job.Status = PrintJobStatus.Discarded;
        job.DiscardReason = reason;
        audit.Write(new AuditRecord("print.job_discarded", "PrintJob", job.Id.ToString(),
            new { status = "Failed" }, new { status = "Discarded", reason, job.Kind, job.TicketId }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
    }

    private async Task<PrintJob> Failed(Guid jobId, CancellationToken ct)
    {
        var job = await db.PrintJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct) ?? throw new NotFoundException("Print job not found.");
        if (job.Status != PrintJobStatus.Failed) throw new ConflictException("Only failed jobs can be retried, moved or discarded.");
        return job;
    }
}
