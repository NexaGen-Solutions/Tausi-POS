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
    public const string SendAndBillMode = "send_and_bill_mode";
}

/// <summary>
/// Printing a ticket from the order screen. Each action runs in one transaction with the ticket
/// row locked and this terminal holding the edit lock, so two presses can't print the same round
/// twice; if a job can't be created, nothing is marked printed. Jobs go through the queue (a
/// printer that is off doesn't undo anything: the order screen shows "not printed · Retry").
/// <list type="bullet">
/// <item><see cref="Print"/> (one button): the bill with every line first, then the dispense slip with only the New lines.</item>
/// <item><see cref="Send"/> and <see cref="PrintBill"/>: the two halves, for <c>SendAndBill</c> mode
/// (<c>print.mode</c>), where the one-button <see cref="Print"/> is refused.</item>
/// <item><see cref="Reprint"/>: the bill again, marked REPRINT; needs <c>ticket.reprint</c>.</item>
/// </list>
/// </summary>
public sealed class PrintTicket(
    ITausiDb db, TicketEditing editing, IPermissionService permissions, IApprovalTokenSource approval,
    TicketPrints prints, Stock.StockSelling stock, IAuditLog audit, TicketEvents events, TimeProvider clock)
{
    public async Task<TicketDetailDto> Print(Guid id, CancellationToken ct)
    {
        if ((await SettingsRegistry.Load(db, ct)).Enum<PrintMode>(SettingsRegistry.PrintModeKey) == PrintMode.SendAndBill)
            throw new ConflictException("This business uses separate Send and Print bill buttons.", PrintCodes.SendAndBillMode);
        return await Run(id, bill: true, dispense: true, ct);
    }

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
        await prints.Reprint(edit.Ticket, edit.TerminalId, grant.ApprovedByUserId, ct);
        await db.SaveChangesAsync(ct);
        await edit.Transaction.CommitAsync(ct);
        await events.Changed(edit.Ticket, edit.Ticket.TableId, ct);
        return await TicketDetails.Mine(db, edit.Ticket, edit.User, ct);
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

        var terminal = await prints.TerminalWithPrinters(edit.TerminalId, ct);
        var receipt = bill ? TicketPrints.Usable(terminal.ReceiptPrinter, terminal.Name, "receipt") : null;
        var dispensePrinter = dispense ? TicketPrints.Usable(terminal.DispensePrinter, terminal.Name, "dispense") : null;
        var now = clock.GetUtcNow();

        if (dispensePrinter is not null)
        {
            ticket.PrintRound++;
            foreach (var line in fresh)
            {
                line.Status = OrderLineStatus.Printed;
                line.PrintRound = ticket.PrintRound;
                line.PrintedAt = now;
                // The round's printer, so a void goes back to the bar that poured it.
                line.DispensePrinterId = dispensePrinter.Id;
            }
            // The bar pours what was sent: its stock leaves this terminal's location now.
            var day = await Day.WorkPeriods.Current(db, ct);
            await stock.Sell(ticket, ticket.PrintRound, fresh, edit.TerminalId, edit.User.UserId, day?.Id, now, ct);
        }
        var jobs = new List<PrintJob>();
        // The bill goes into the queue first, so where one printer does both, it comes out first.
        if (receipt is not null)
        {
            ticket.BillNumber++;
            ticket.BilledRound = ticket.PrintRound;
            ticket.BillPrintedAt = now;
            jobs.Add(await prints.Bill(ticket, terminal, receipt, reprint: false, corrected: false, now, ct));
        }
        if (dispensePrinter is not null)
            jobs.Add(await prints.Dispense(ticket, terminal, dispensePrinter, fresh, now, ct));
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

    /// <summary>"Table 7", "Pool 1", "Takeaway · Otieno" or "Takeaway".</summary>
    public static string Where(Ticket ticket) => TicketPrints.Where(ticket);
}

/// <summary>
/// The ticket's printouts, shared by printing, voids, gifts, discounts, price changes and approved
/// requests: bills (also reprints and corrected bills), dispense slips and VOID slips. Jobs are
/// added to the queue in the caller's transaction.
/// </summary>
public sealed class TicketPrints(
    ITausiDb db, PrintQueue queue, ILogoProvider logos, IAuditLog audit, TimeProvider clock, BusinessClock businessClock)
{
    /// <summary>The bill again, marked REPRINT, on this terminal's receipt printer.</summary>
    public async Task<PrintJob> Reprint(Ticket ticket, Guid terminalId, Guid? approvedBy, CancellationToken ct)
    {
        if (!ticket.Lines.Any(l => l.Counts)) throw new DomainException("There is nothing on this ticket to bill.");
        var terminal = await TerminalWithPrinters(terminalId, ct);
        var receipt = Usable(terminal.ReceiptPrinter, terminal.Name, "receipt");
        var now = clock.GetUtcNow();
        ticket.BillNumber++;
        ticket.BillPrintedAt = now;
        var job = await Bill(ticket, terminal, receipt, reprint: true, corrected: false, now, ct);
        audit.Write(new AuditRecord(AuditActions.BillReprinted, nameof(Ticket), ticket.Id.ToString(), null,
            new { ticket = ticket.Number, bill = ticket.BillNumber, total = ticket.Total, printJobId = job.Id }, approvedBy));
        return job;
    }

    /// <summary>
    /// After a void, gift, discount or price change on a ticket that has had a bill: a corrected bill
    /// with the next number, on the requesting terminal's receipt printer. Without a usable printer
    /// nothing prints (the change still stands) and null comes back.
    /// </summary>
    public async Task<PrintJob?> CorrectedBillIfBilled(Ticket ticket, Guid terminalId, CancellationToken ct)
    {
        if (ticket.BillNumber == 0) return null;
        var terminal = await TerminalWithPrinters(terminalId, ct);
        if (terminal.ReceiptPrinter is not { Active: true } receipt) return null;
        var now = clock.GetUtcNow();
        ticket.BillNumber++;
        ticket.BillPrintedAt = now;
        return await Bill(ticket, terminal, receipt, reprint: false, corrected: true, now, ct);
    }

    /// <summary>
    /// A VOID slip to the dispense printer that printed the line (else the requesting terminal's).
    /// Without a usable printer nothing prints and null comes back.
    /// </summary>
    public async Task<PrintJob?> VoidSlip(Ticket ticket, OrderLine voided, Guid terminalId, string reason,
        string requestedBy, string approvedBy, CancellationToken ct)
    {
        var terminal = await TerminalWithPrinters(terminalId, ct);
        var printer = voided.DispensePrinterId is { } id
            ? await db.Printers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            : terminal.DispensePrinter;
        if (printer is not { Active: true }) return null;
        var header = printer.DispenseHeader
            ?? await db.Terminals.AsNoTracking().Where(t => t.DispensePrinterId == printer.Id).Select(t => t.DispenseHeader ?? t.Name).FirstOrDefaultAsync(ct)
            ?? printer.Name;
        var slip = new Printing.VoidSlip(header, Where(ticket), ticket.Number, BusinessClock.ToLocal(clock.GetUtcNow(), await businessClock.Zone(ct)),
            [ToSlipLine(voided)], reason, requestedBy, approvedBy);
        return queue.Enqueue(printer, PrintJobKind.Void, Slips.Void(slip, SlipStyle.For(printer, null)), ticket.Id);
    }

    /// <summary>
    /// The final receipt on this terminal's receipt printer (COPY for a reprint). The cash drawer
    /// opens with it when the printer has "open cash drawer on receipt" on and cash was taken,
    /// never for a copy. Without a usable printer nothing prints and null comes back.
    /// </summary>
    public async Task<PrintJob?> Receipt(Ticket ticket, Guid terminalId, bool copy, CancellationToken ct)
    {
        var terminal = await TerminalWithPrinters(terminalId, ct);
        if (terminal.ReceiptPrinter is not { Active: true } printer) return null;
        var business = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct) ?? new BusinessSettings();
        var values = SettingsRegistry.Read(business);
        var settings = PrintSettings.From(values);
        var logo = settings.LogoOnBill ? await logos.GetLogo(ReceiptBuilder.LogoWidthFor(printer.PaperWidthMm), ct) : null;
        var payments = ticket.Payments.Where(p => p.Status == PaymentStatus.Final).OrderBy(p => p.TakenAt).ToList();
        var names = new Dictionary<Guid, string>();
        async Task<string> Name(Guid id) => names.TryGetValue(id, out var n) ? n : names[id] = await UserName(id, ct);

        // One query at a time: the context isn't safe for parallel use.
        var receiptPayments = new List<ReceiptPayment>();
        foreach (var p in payments)
            receiptPayments.Add(new ReceiptPayment(
                p.Type == PaymentType.Mpesa ? "M-Pesa" : p.Type.ToString(), p.Amount, p.Tendered, p.Change,
                p.Type == PaymentType.Mpesa ? p.MpesaCode : p.CardReference, await Name(p.BroughtByUserId)));
        var slip = new ReceiptSlip(
            new BusinessInfo(business.Name, business.Address, business.Phone, business.ReceiptFooter),
            ticket.ReceiptNumber ?? 0, ticket.Number, copy, Where(ticket), await Name(ticket.OpenedByUserId),
            ticket.SettledByUserId is { } cashier ? await Name(cashier) : "?", terminal.Name,
            BusinessClock.ToLocal(ticket.SettledAt ?? clock.GetUtcNow(), BusinessTimeZone.Find(business.TimeZone)),
            ReceiptLines(ticket, settings.ShowVoidsOnReceipt),
            DiscountLabel(ticket), ticket.RoundingAmount,
            receiptPayments,
            OpenDrawer: !copy && printer.OpenDrawerOnReceipt && payments.Any(p => p.Type == PaymentType.Cash),
            ShowBroughtBy: PaymentSettings.From(values).RecordBroughtBy);
        var document = Slips.Receipt(slip, SlipStyle.For(printer, logo, business.Currency, settings.PoweredByFooter));
        return queue.Enqueue(printer, PrintJobKind.Receipt, document, ticket.Id);
    }

    public Task<Terminal> TerminalWithPrinters(Guid terminalId, CancellationToken ct) =>
        db.Terminals.AsNoTracking().Include(t => t.ReceiptPrinter).Include(t => t.DispensePrinter).FirstAsync(t => t.Id == terminalId, ct);

    public static Printer Usable(Printer? printer, string terminal, string role)
    {
        if (printer is null)
            throw new DomainException($"{terminal} has no {role} printer. Set one in the admin site under Terminals and printers.", PrintCodes.NoPrinter);
        if (!printer.Active)
            throw new DomainException($"{printer.Name} ({role} printer for {terminal}) is switched off.", PrintCodes.NoPrinter);
        return printer;
    }

    public async Task<PrintJob> Bill(Ticket ticket, Terminal terminal, Printer printer, bool reprint, bool corrected, DateTimeOffset now, CancellationToken ct)
    {
        var business = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct) ?? new BusinessSettings();
        var settings = PrintSettings.From(SettingsRegistry.Read(business));
        var logo = settings.LogoOnBill ? await logos.GetLogo(ReceiptBuilder.LogoWidthFor(printer.PaperWidthMm), ct) : null;
        var waiter = await UserName(ticket.OpenedByUserId, ct);
        var slip = new BillSlip(
            new BusinessInfo(business.Name, business.Address, business.Phone, business.ReceiptFooter),
            ticket.BillNumber, ticket.Number, reprint, Where(ticket), waiter, terminal.Name,
            BusinessClock.ToLocal(now, BusinessTimeZone.Find(business.TimeZone)),
            ticket.Lines.Where(l => l.Counts).OrderBy(l => l.AddedAt).Select(ToSlipLine).ToList(),
            corrected, DiscountLabel(ticket));
        var document = Slips.Bill(slip, SlipStyle.For(printer, logo, business.Currency, settings.PoweredByFooter));
        return queue.Enqueue(printer, PrintJobKind.Bill, document, ticket.Id);
    }

    public async Task<PrintJob> Dispense(
        Ticket ticket, Terminal terminal, Printer printer, IReadOnlyList<OrderLine> lines, DateTimeOffset now, CancellationToken ct)
    {
        var groups = new List<DispenseGroup>();
        foreach (var byWaiter in lines.GroupBy(l => l.AddedByUserId))
            groups.Add(new DispenseGroup(await UserName(byWaiter.Key, ct), byWaiter.Select(ToSlipLine).ToList()));
        var header = terminal.DispenseHeader ?? printer.DispenseHeader ?? terminal.Name;
        var at = BusinessClock.ToLocal(now, await businessClock.Zone(ct));
        var slip = new DispenseSlip(header, Where(ticket), ticket.Number, at, groups, string.IsNullOrWhiteSpace(ticket.Note) ? null : ticket.Note);
        return queue.Enqueue(printer, PrintJobKind.Dispense, Slips.Dispense(slip, SlipStyle.For(printer, null)), ticket.Id);
    }

    /// <summary>"Discount 10% (Regular customer)" or "Discount (Promotion)".</summary>
    public static string? DiscountLabel(Ticket ticket) => ticket.DiscountKind switch
    {
        null => null,
        DiscountKind.Percent => $"Discount {ticket.DiscountValue:0.##}%" + (ticket.DiscountReason is { } r ? $" ({r})" : ""),
        _ => "Discount" + (ticket.DiscountReason is { } r ? $" ({r})" : ""),
    };

    /// <summary>The receipt's lines in the order they were added; voided ones too (marked VOID) when the setting says so.</summary>
    public static IReadOnlyList<SlipLine> ReceiptLines(Ticket ticket, bool showVoids) =>
        ticket.Lines.Where(l => l.Counts || (showVoids && l.IsVoided)).OrderBy(l => l.AddedAt)
            .Select(l => l.IsVoided ? ToSlipLine(l) with { IsVoid = true, Amount = 0, Vat = 0, Discount = 0 } : ToSlipLine(l))
            .ToList();

    public static SlipLine ToSlipLine(OrderLine l) =>
        new(l.Quantity, l.Name, l.Options, l.Gross, l.UnitPrice, l.VatRate, l.Vat, string.IsNullOrEmpty(l.ShortName) ? l.Name : l.ShortName,
            l.Note, l.VatExempt, l.IsGift, l.Discount);

    public async Task<string> UserName(Guid id, CancellationToken ct) =>
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
