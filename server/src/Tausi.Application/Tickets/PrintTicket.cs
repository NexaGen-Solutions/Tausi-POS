using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.Devices;
using Tausi.Domain.People;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;
using Tausi.Domain.Settings;

namespace Tausi.Application.Tickets;

public static class PrintCodes
{
    public const string NothingNew = "nothing_new";
    public const string NoPrinter = "no_printer";
}

/// <summary>
/// Printing a ticket from the order screen. Each action runs in one transaction with the ticket
/// row locked and this terminal holding the edit lock, so two presses can't print the same round
/// twice; if a job can't be created, nothing is marked printed. Jobs go through the queue (a
/// printer that is off doesn't undo anything: the order screen shows "not printed · Retry").
/// <list type="bullet">
/// <item><see cref="Print"/> (one button): the bill with every line first, then the dispense slip with only the New lines.</item>
/// <item><see cref="Send"/> and <see cref="PrintBill"/>: the two halves, for <c>SendAndBill</c> mode.</item>
/// <item><see cref="Reprint"/>: the bill again, marked REPRINT; needs <c>ticket.reprint</c>.</item>
/// </list>
/// </summary>
public sealed class PrintTicket(
    ITausiDb db, TicketEditing editing, IPermissionService permissions, IApprovalTokenSource approval,
    PrintQueue queue, ILogoProvider logos, IAuditLog audit, TicketEvents events, TimeProvider clock, BusinessClock businessClock)
{
    public Task<TicketDetailDto> Print(Guid id, CancellationToken ct) => Run(id, bill: true, dispense: true, ct);

    public Task<TicketDetailDto> Send(Guid id, CancellationToken ct) => Run(id, bill: false, dispense: true, ct);

    /// <summary>The bill alone; with nothing printed or added since the last bill, it is a reprint.</summary>
    public async Task<TicketDetailDto> PrintBill(Guid id, CancellationToken ct)
    {
        var current = await db.Tickets.AsNoTracking().Where(k => k.Id == id)
            .Select(k => new { k.BillNumber, k.BilledRound, k.PrintRound, New = k.Lines.Count(l => l.Status == OrderLineStatus.New) })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Ticket not found.");
        return current.BillNumber > 0 && current.BilledRound == current.PrintRound && current.New == 0
            ? await Reprint(id, ct)
            : await Run(id, bill: true, dispense: false, ct);
    }

    public async Task<TicketDetailDto> Reprint(Guid id, CancellationToken ct)
    {
        await using var edit = await editing.Open(id, ct);
        var grant = await permissions.Require(edit.User, PermissionKeys.TicketReprint, approval.ApprovalToken, ct);
        var ticket = edit.Ticket;
        if (!ticket.Lines.Any(l => l.Counts)) throw new DomainException("There is nothing on this ticket to bill.");
        var terminal = await TerminalWithPrinters(edit.TerminalId, ct);
        var receipt = Usable(terminal.ReceiptPrinter, terminal.Name, "receipt");
        var now = clock.GetUtcNow();

        ticket.BillNumber++;
        ticket.BillPrintedAt = now;
        var job = queue.Enqueue(receipt, PrintJobKind.Bill, await BillDocument(ticket, terminal, receipt, reprint: true, now, ct), ticket.Id);
        audit.Write(new AuditRecord(AuditActions.BillReprinted, nameof(Ticket), ticket.Id.ToString(), null,
            new { ticket = ticket.Number, bill = ticket.BillNumber, total = ticket.Total, printJobId = job.Id }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await edit.Transaction.CommitAsync(ct);
        await events.Changed(ticket, ticket.TableId, ct);
        return await TicketDetails.Mine(db, ticket, edit.User, ct);
    }

    private async Task<TicketDetailDto> Run(Guid id, bool bill, bool dispense, CancellationToken ct)
    {
        await using var edit = await editing.Open(id, ct);
        var grant = await permissions.Require(edit.User, PermissionKeys.TicketPrint, approval.ApprovalToken, ct);
        var ticket = edit.Ticket;
        var fresh = ticket.Lines.Where(l => l.Status == OrderLineStatus.New).OrderBy(l => l.AddedAt).ToList();
        if (dispense && fresh.Count == 0)
            throw new ConflictException("There is nothing new to print.", PrintCodes.NothingNew);
        if (!dispense && !ticket.Lines.Any(l => l.Counts))
            throw new DomainException("There is nothing on this ticket to bill.");

        var terminal = await TerminalWithPrinters(edit.TerminalId, ct);
        var receipt = bill ? Usable(terminal.ReceiptPrinter, terminal.Name, "receipt") : null;
        var dispensePrinter = dispense ? Usable(terminal.DispensePrinter, terminal.Name, "dispense") : null;
        var now = clock.GetUtcNow();

        if (dispense)
        {
            ticket.PrintRound++;
            foreach (var line in fresh)
            {
                line.Status = OrderLineStatus.Printed;
                line.PrintRound = ticket.PrintRound;
                line.PrintedAt = now;
            }
        }
        var jobs = new List<PrintJob>();
        // The bill goes into the queue first, so where one printer does both, it comes out first.
        if (receipt is not null)
        {
            ticket.BillNumber++;
            ticket.BilledRound = ticket.PrintRound;
            ticket.BillPrintedAt = now;
            jobs.Add(queue.Enqueue(receipt, PrintJobKind.Bill, await BillDocument(ticket, terminal, receipt, reprint: false, now, ct), ticket.Id));
        }
        if (dispensePrinter is not null)
            jobs.Add(queue.Enqueue(dispensePrinter, PrintJobKind.Dispense, await DispenseDocument(ticket, terminal, dispensePrinter, fresh, now, ct), ticket.Id));
        ticket.Touch(now);

        audit.Write(new AuditRecord(AuditActions.TicketPrinted, nameof(Ticket), ticket.Id.ToString(), null,
            new
            {
                ticket = ticket.Number,
                bill = receipt is null ? (int?)null : ticket.BillNumber,
                round = dispense ? ticket.PrintRound : (int?)null,
                lines = fresh.Count,
                total = ticket.Total,
                jobs = jobs.Select(j => new { j.Id, kind = j.Kind.ToString() }),
            }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await edit.Transaction.CommitAsync(ct);
        await events.Changed(ticket, ticket.TableId, ct);
        return await TicketDetails.Mine(db, ticket, edit.User, ct);
    }

    private Task<Terminal> TerminalWithPrinters(Guid terminalId, CancellationToken ct) =>
        db.Terminals.AsNoTracking().Include(t => t.ReceiptPrinter).Include(t => t.DispensePrinter).FirstAsync(t => t.Id == terminalId, ct);

    private static Printer Usable(Printer? printer, string terminal, string role)
    {
        if (printer is null)
            throw new DomainException($"{terminal} has no {role} printer. Set one in the admin site under Terminals and printers.", PrintCodes.NoPrinter);
        if (!printer.Active)
            throw new DomainException($"{printer.Name} ({role} printer for {terminal}) is switched off.", PrintCodes.NoPrinter);
        return printer;
    }

    private async Task<PrintDocument> BillDocument(Ticket ticket, Terminal terminal, Printer printer, bool reprint, DateTimeOffset now, CancellationToken ct)
    {
        var business = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct) ?? new BusinessSettings();
        var settings = PrintSettings.From(business.OtherJson);
        var logo = settings.LogoOnBill ? await logos.GetLogo(ReceiptBuilder.LogoWidthFor(printer.PaperWidthMm), ct) : null;
        var waiter = await UserName(ticket.OpenedByUserId, ct);
        var slip = new BillSlip(
            new BusinessInfo(business.Name, business.Address, business.Phone, business.ReceiptFooter),
            ticket.BillNumber, ticket.Number, reprint, Where(ticket), waiter, terminal.Name,
            BusinessClock.ToLocal(now, BusinessTimeZone.Find(business.TimeZone)),
            ticket.Lines.Where(l => l.Counts).OrderBy(l => l.AddedAt).Select(ToSlipLine).ToList());
        return Slips.Bill(slip, SlipStyle.For(printer, logo, business.Currency, settings.PoweredByFooter));
    }

    private async Task<PrintDocument> DispenseDocument(
        Ticket ticket, Terminal terminal, Printer printer, IReadOnlyList<OrderLine> lines, DateTimeOffset now, CancellationToken ct)
    {
        var groups = new List<DispenseGroup>();
        foreach (var byWaiter in lines.GroupBy(l => l.AddedByUserId))
            groups.Add(new DispenseGroup(await UserName(byWaiter.Key, ct), byWaiter.Select(ToSlipLine).ToList()));
        var header = terminal.DispenseHeader ?? printer.DispenseHeader ?? terminal.Name;
        var at = BusinessClock.ToLocal(now, await businessClock.Zone(ct));
        var slip = new DispenseSlip(header, Where(ticket), ticket.Number, at, groups, string.IsNullOrWhiteSpace(ticket.Note) ? null : ticket.Note);
        return Slips.Dispense(slip, SlipStyle.For(printer, null));
    }

    private static SlipLine ToSlipLine(OrderLine l) =>
        new(l.Quantity, l.Name, l.Options, l.Gross, l.UnitPrice, l.VatRate, l.Vat, string.IsNullOrEmpty(l.ShortName) ? l.Name : l.ShortName, l.Note, l.VatExempt);

    private async Task<string> UserName(Guid id, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "?";

    /// <summary>"Table 7", "Pool 1", "Takeaway · Otieno" or "Takeaway".</summary>
    public static string Where(Ticket ticket) => ticket.Table is { } table
        ? (table.Name.All(char.IsDigit) ? $"Table {table.Name}" : table.Name)
        : string.IsNullOrWhiteSpace(ticket.CustomerName) ? "Takeaway" : $"Takeaway · {ticket.CustomerName}";
}

/// <summary>
/// Retries a failed job of this ticket from the order screen ("Bill not printed · Retry"). It keeps
/// its place in the queue, so a bill still prints before its dispense slip. Needs <c>ticket.print</c>.
/// </summary>
public sealed class RetryTicketPrint(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    IAuditLog audit, TicketEvents events, TimeProvider clock)
{
    public async Task Execute(Guid ticketId, Guid jobId, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.TicketPrint, approval.ApprovalToken, ct);
        var job = await db.PrintJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.TicketId == ticketId, ct)
            ?? throw new NotFoundException("That print job is not on this ticket.");
        if (job.Status != PrintJobStatus.Failed) return;
        job.Requeue(clock.GetUtcNow());
        audit.Write(new AuditRecord(AuditActions.PrintJobRetried, nameof(PrintJob), job.Id.ToString(), null,
            new { job.Kind, ticketId }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        var ticket = await db.Tickets.AsNoTracking().FirstAsync(k => k.Id == ticketId, ct);
        await events.Changed(ticket, ticket.TableId, ct);
    }
}
