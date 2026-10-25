using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Money;
using Tausi.Application.Security;
using Tausi.Application.Settling;
using Tausi.Application.Stock;
using Tausi.Domain.Menu;
using Tausi.Domain.People;
using Tausi.Domain.Service;
using Tausi.Domain.Stock;

namespace Tausi.Application.Reports;

public sealed record TicketHeaderDto(
    Guid Id, long Number, long? ReceiptNumber, string? Table, string Status, string OpenedBy, DateTimeOffset OpenedAt, string OpenedOn,
    string? SettledBy, DateTimeOffset? SettledAt, string? SettledOn, DateOnly? BusinessDate, DateTimeOffset PeriodOpenedAt,
    decimal Total, decimal Discount, string? DiscountLabel, decimal Rounding, long? MergedIntoNumber, Guid? MergedIntoId,
    IReadOnlyList<long> MergedInNumbers, string Note);

public sealed record LinePriceChangeDto(decimal? OldPrice, decimal? NewPrice, string By, string? ApprovedBy, DateTimeOffset At, string? Reason);

public sealed record TicketLineDto(
    Guid Id, string Name, string? Options, string? Note, decimal Quantity, decimal UnitPrice, decimal? OriginalUnitPrice, decimal Gross,
    decimal Discount, decimal Amount, string Status, bool Gift, string AddedBy, string Terminal, DateTimeOffset AddedAt, int? PrintRound,
    DateTimeOffset? PrintedAt, string? VoidedBy, DateTimeOffset? VoidedAt, string? VoidReason, string? CancelledBy, DateTimeOffset? CancelledAt,
    IReadOnlyList<LinePriceChangeDto> PriceChanges);

/// <summary>A print job for the ticket; <c>Text</c> is exactly what was printed (null once cleared after print.keepSlipTextDays).</summary>
public sealed record TicketPrintDto(
    Guid Id, string Kind, string Label, string Printer, DateTimeOffset At, DateTimeOffset? SentAt, string? By, string Status, bool Reprint,
    string? Text, string? Error);

public sealed record TicketPaymentDto(
    Guid Id, string Type, decimal Amount, decimal? Tendered, decimal? Change, string? Reference, string TakenBy, string BroughtBy, string? Shift,
    DateTimeOffset At, string Status, string? RemovedBy, DateTimeOffset? RemovedAt);

public sealed record TicketApprovalDto(
    Guid Id, string Kind, string Summary, decimal? Amount, string? Reason, string RequestedBy, DateTimeOffset RequestedAt, string Status,
    string? DecidedBy, DateTimeOffset? DecidedAt, string? Note);

public sealed record TicketMovementDto(string Item, string Location, string Kind, decimal Quantity, string QuantityText, decimal UnitCost, decimal Value,
    DateTimeOffset At);

public sealed record TicketJournalLineDto(string Code, string Account, decimal Debit, decimal Credit);

public sealed record TicketJournalEntryDto(Guid Id, DateOnly? BusinessDate, DateTimeOffset At, string Memo, IReadOnlyList<TicketJournalLineDto> Lines);

/// <summary>One thing that happened, for the timeline; <c>Ref</c> points at the line, print, payment or approval it is about.</summary>
public sealed record TimelineEventDto(DateTimeOffset At, string Kind, string Text, string? By, string? Ref);

public sealed record TicketReportDto(
    TicketHeaderDto Ticket, IReadOnlyList<TicketLineDto> Lines, IReadOnlyList<TicketPrintDto> Prints, IReadOnlyList<TicketPaymentDto> Payments,
    IReadOnlyList<TicketApprovalDto> Approvals, IReadOnlyList<TicketMovementDto> Movements, IReadOnlyList<TicketJournalEntryDto> Journal,
    IReadOnlyList<TimelineEventDto> Timeline);

/// <summary>
/// The ticket detail (read-only, reports.all): the header, every line in every state, every print
/// with its exact text, payments (removed ones too), approval requests, the stock movements and
/// journal entries it caused, and all of it merged into one timeline.
/// </summary>
public sealed class TicketDetailReport(ITausiDb db, ICurrentSession session, IPermissionService permissions)
{
    public async Task<TicketReportDto> Execute(Guid id, CancellationToken ct)
    {
        await RequireReports(session, permissions, ct);
        var ticket = await db.Tickets.AsNoTracking().Include(k => k.Table).FirstOrDefaultAsync(k => k.Id == id, ct)
            ?? throw new NotFoundException("Ticket not found.");
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var terminals = await db.Terminals.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        string Name(Guid? user) => user is { } u ? users.GetValueOrDefault(u, "?") : "";
        string? NameOrNull(Guid? user) => user is { } u ? users.GetValueOrDefault(u, "?") : null;
        string Terminal(Guid? terminal) => terminal is { } t ? terminals.GetValueOrDefault(t, "?") : "";

        var lines = await db.OrderLines.AsNoTracking().Where(l => l.TicketId == id).OrderBy(l => l.AddedAt).ToListAsync(ct);
        var mergedIn = await db.Tickets.AsNoTracking().Where(k => k.MergedIntoTicketId == id).Select(k => new { k.Id, k.Number }).ToListAsync(ct);
        var period = await db.WorkPeriods.AsNoTracking().FirstAsync(p => p.Id == ticket.WorkPeriodId, ct);
        var lineIds = lines.Select(l => l.Id).ToList();
        var priceChanges = await db.PriceChangeLogs.AsNoTracking().Where(p => p.Kind == PriceChangeKind.Ticket && p.OrderLineId != null && lineIds.Contains(p.OrderLineId.Value))
            .OrderBy(p => p.At).ToListAsync(ct);
        var voids = await db.VoidLogs.AsNoTracking().Where(v => v.TicketId == id || lineIds.Contains(v.OrderLineId)).ToListAsync(ct);
        var gifts = await db.GiftLogs.AsNoTracking().Where(g => g.TicketId == id || lineIds.Contains(g.OrderLineId)).ToListAsync(ct);
        var discounts = await db.DiscountLogs.AsNoTracking().Where(d => d.TicketId == id).ToListAsync(ct);

        var lineDtos = lines.Select(l => new TicketLineDto(l.Id, l.Name, l.Options, l.Note, l.Quantity, l.UnitPrice, l.OriginalUnitPrice, l.Gross,
            l.Discount, l.Counts ? l.Payable : 0, LineStatus(l), l.IsGift, Name(l.AddedByUserId), Terminal(l.AddedTerminalId), l.AddedAt, l.PrintRound,
            l.PrintedAt, NameOrNull(l.VoidedByUserId), l.VoidedAt, l.VoidReason, NameOrNull(l.CancelledByUserId), l.CancelledAt,
            priceChanges.Where(p => p.OrderLineId == l.Id)
                .Select(p => new LinePriceChangeDto(p.OldPrice, p.NewPrice, Name(p.ChangedByUserId), NameOrNull(p.ApprovedByUserId), p.At, p.Reason)).ToList()))
            .ToList();

        var prints = await db.PrintJobs.AsNoTracking().Where(j => j.TicketId == id).OrderBy(j => j.CreatedAt)
            .Select(j => new { j.Id, j.Kind, j.Label, Printer = j.Printer!.Name, j.CreatedAt, j.SentAt, j.CreatedByUserId, j.Status, j.IsReprint, j.TextPreview, j.SlipClearedAt, j.LastError })
            .ToListAsync(ct);
        var printDtos = prints.Select(j => new TicketPrintDto(j.Id, j.Kind.ToString(), j.Label ?? j.Kind.ToString(), j.Printer, j.CreatedAt, j.SentAt,
            NameOrNull(j.CreatedByUserId), j.Status.ToString(), j.IsReprint, j.SlipClearedAt is null ? j.TextPreview : null, j.LastError)).ToList();

        var payments = await db.Payments.AsNoTracking().Where(p => p.TicketId == id).OrderBy(p => p.TakenAt).ToListAsync(ct);
        var shiftIds = payments.Where(p => p.ShiftId != null).Select(p => p.ShiftId!.Value).Distinct().ToList();
        var shifts = await db.CashierShifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => $"{Name(s.CashierUserId)} · {Terminal(s.TerminalId)}", ct);
        var paymentDtos = payments.Select(p => new TicketPaymentDto(p.Id, p.Type == PaymentType.Mpesa ? "M-Pesa" : p.Type.ToString(), p.Amount, p.Tendered,
            p.Change, p.Type == PaymentType.Mpesa ? p.MpesaCode : p.CardReference, Name(p.TakenByUserId), Name(p.BroughtByUserId),
            p.ShiftId is { } s ? shifts.GetValueOrDefault(s) : null, p.TakenAt, p.Status.ToString(), NameOrNull(p.RemovedByUserId), p.RemovedAt)).ToList();

        var approvals = await db.ApprovalRequests.AsNoTracking().Where(r => r.TicketId == id).OrderBy(r => r.RequestedAt).ToListAsync(ct);
        var approvalDtos = approvals.Select(r => new TicketApprovalDto(r.Id, r.Kind == ApprovalKind.PriceChange ? "Price change" : r.Kind.ToString(),
            r.Summary, r.Amount, r.Reason, Name(r.RequestedByUserId), r.RequestedAt, r.Status.ToString(), NameOrNull(r.DecidedByUserId), r.DecidedAt,
            r.DecisionNote)).ToList();

        // Stock: the sale movements of each line (and of the lines they were split from) and voids returned.
        var saleIds = lines.Select(l => l.SaleLineId).Concat(lineIds).Distinct().ToList();
        var items = await db.StockItems.AsNoTracking().ToDictionaryAsync(i => i.Id, ct);
        var locations = await db.StockLocations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var movements = await db.StockMovements.AsNoTracking()
            .Where(m => m.ReferenceType == StockSelling.SaleReference && m.ReferenceId != null && saleIds.Contains(m.ReferenceId.Value))
            .OrderBy(m => m.At).ToListAsync(ct);
        var movementDtos = movements.Select(m => new TicketMovementDto(items.TryGetValue(m.StockItemId, out var i) ? i.Name : "?",
            locations.GetValueOrDefault(m.LocationId, "?"), m.Kind == StockMovementKind.VoidReturn ? "Void returned" : m.Kind.ToString(), m.Quantity,
            i?.Describe(m.Quantity) ?? m.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture), m.UnitCost,
            Domain.Money.MoneyMath.Round(m.Quantity * m.UnitCost), m.At)).ToList();

        // The ledger: the sale, cost of sales per print round, voided pours and returns (also for tickets merged in).
        var ticketIds = mergedIn.Select(k => k.Id.ToString()).Append(id.ToString()).ToList();
        var lineSources = lineIds.Select(l => l.ToString()).ToList();
        var candidates = await db.JournalEntries.AsNoTracking()
            .Where(e => (e.SourceType == SalePosting.SourceType && ticketIds.Contains(e.SourceId!))
                        || e.SourceType == StockSelling.SaleSource
                        || (e.SourceType == StockSelling.VoidSource && lineSources.Contains(e.SourceId!)))
            .Where(e => e.SourceType != StockSelling.SaleSource || e.WorkPeriodId == ticket.WorkPeriodId)
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .ToListAsync(ct);
        var journal = candidates
            .Where(e => e.SourceType != StockSelling.SaleSource || ticketIds.Any(t => e.SourceId!.StartsWith(t + ":", StringComparison.Ordinal)))
            .OrderBy(e => e.CreatedAt)
            .Select(e => new TicketJournalEntryDto(e.Id, e.BusinessDate, e.CreatedAt, e.Memo,
                e.Lines.Select(l => new TicketJournalLineDto(l.Account!.Code, l.Account.Name, l.Debit, l.Credit)).ToList()))
            .ToList();

        var audits = await db.AuditEntries.AsNoTracking()
            .Where(a => a.EntityId == id.ToString() && (a.Action == AuditActions.TicketMoved || a.Action == AuditActions.TicketMerged))
            .ToListAsync(ct);

        var timeline = new List<TimelineEventDto>
        {
            new(ticket.OpenedAt, "Opened", $"Opened on {Terminal(ticket.TerminalId)}", Name(ticket.OpenedByUserId), null),
        };
        // Each line as it was added: parts split off later (for a void, gift or price change) count back in.
        timeline.AddRange(lines.Where(l => l.SplitFromLineId is null)
            .Select(l => new TimelineEventDto(l.AddedAt, "Added",
                $"{Qty(l.Quantity + lines.Where(p => p.SplitFromLineId == l.Id).Sum(p => p.Quantity))} × {l.Name}{(l.Options is null ? "" : $" ({l.Options})")}",
                Name(l.AddedByUserId), l.Id.ToString())));
        timeline.AddRange(lines.Where(l => l.CancelledAt is not null)
            .Select(l => new TimelineEventDto(l.CancelledAt!.Value, "Cancelled", $"{Qty(l.Quantity)} × {l.Name} taken off before printing", Name(l.CancelledByUserId), l.Id.ToString())));
        timeline.AddRange(voids.Select(v => new TimelineEventDto(v.At, "Void",
            $"{Qty(v.Quantity)} × {v.ItemName} voided, KSh {ReportFormat.Money(v.Amount)} · {v.Reason}{(v.WasPoured ? " · poured" : " · not poured")}",
            $"{Name(v.RequestedByUserId)}, approved by {Name(v.ApprovedByUserId)}", v.OrderLineId.ToString())));
        timeline.AddRange(gifts.Select(g => new TimelineEventDto(g.At, "Gift", $"{Qty(g.Quantity)} × {g.ItemName} given free (KSh {ReportFormat.Money(g.Amount)}) · {g.Reason}",
            $"{Name(g.RequestedByUserId)}, approved by {Name(g.ApprovedByUserId)}", g.OrderLineId.ToString())));
        timeline.AddRange(discounts.Select(d => new TimelineEventDto(d.At, "Discount",
            d.Kind is null ? "Discount removed" : $"Discount {(d.Kind == DiscountKind.Percent ? $"{d.Value:0.##}%" : $"KSh {ReportFormat.Money(d.Value)}")} (KSh {ReportFormat.Money(d.Amount)}){(d.Reason is null ? "" : $" · {d.Reason}")}",
            $"{Name(d.RequestedByUserId)}, approved by {Name(d.ApprovedByUserId)}", null)));
        timeline.AddRange(priceChanges.Select(p => new TimelineEventDto(p.At, "Price change",
            $"{lines.FirstOrDefault(l => l.Id == p.OrderLineId)?.Name}: KSh {ReportFormat.Money(p.OldPrice ?? 0)} to KSh {ReportFormat.Money(p.NewPrice ?? 0)}",
            p.ApprovedByUserId is { } a && a != p.ChangedByUserId ? $"{Name(p.ChangedByUserId)}, approved by {Name(a)}" : Name(p.ChangedByUserId),
            p.OrderLineId?.ToString())));
        timeline.AddRange(prints.Select(j => new TimelineEventDto(j.CreatedAt, "Print", $"{j.Label ?? j.Kind.ToString()} on {j.Printer} · {j.Status}",
            NameOrNull(j.CreatedByUserId), j.Id.ToString())));
        timeline.AddRange(approvals.Select(r => new TimelineEventDto(r.RequestedAt, "Request", $"Asked: {r.Summary}", Name(r.RequestedByUserId), r.Id.ToString())));
        timeline.AddRange(approvals.Where(r => r.DecidedAt is not null).Select(r => new TimelineEventDto(r.DecidedAt!.Value, "Request",
            $"{r.Status}: {r.Summary}{(r.DecisionNote is null ? "" : $" · {r.DecisionNote}")}", NameOrNull(r.DecidedByUserId), r.Id.ToString())));
        timeline.AddRange(payments.Select(p => new TimelineEventDto(p.TakenAt, "Payment",
            $"{(p.Type == PaymentType.Mpesa ? "M-Pesa" : p.Type.ToString())} KSh {ReportFormat.Money(p.Amount)}{(p.MpesaCode is null ? "" : $" · {p.MpesaCode}")}",
            Name(p.TakenByUserId), p.Id.ToString())));
        timeline.AddRange(payments.Where(p => p.RemovedAt is not null).Select(p => new TimelineEventDto(p.RemovedAt!.Value, "Payment",
            $"Removed {(p.Type == PaymentType.Mpesa ? "M-Pesa" : p.Type.ToString())} KSh {ReportFormat.Money(p.Amount)}", NameOrNull(p.RemovedByUserId), p.Id.ToString())));
        timeline.AddRange(audits.Select(a => new TimelineEventDto(a.At, a.Action == AuditActions.TicketMoved ? "Moved" : "Merged",
            a.Action == AuditActions.TicketMoved ? "Moved to another table" : $"Tickets merged in: {string.Join(", ", mergedIn.Select(k => "#" + k.Number))}",
            Name(a.UserId), null)));
        if (ticket.SettledAt is { } settled)
            timeline.Add(new TimelineEventDto(settled, "Settled", $"Settled on {Terminal(ticket.SettledTerminalId)}, receipt {ticket.ReceiptNumber}",
                Name(ticket.SettledByUserId), null));
        if (ticket.MergedIntoTicketId is { } into)
            timeline.Add(new TimelineEventDto(ticket.LastActivityAt, "Merged",
                $"Merged into #{await db.Tickets.AsNoTracking().Where(k => k.Id == into).Select(k => k.Number).FirstOrDefaultAsync(ct)}", null, null));

        var counted = lines.Where(l => l.Counts).ToList();
        var header = new TicketHeaderDto(ticket.Id, ticket.Number, ticket.ReceiptNumber, ticket.Table?.Name ?? ticket.CustomerName, ticket.Status.ToString(),
            Name(ticket.OpenedByUserId), ticket.OpenedAt, Terminal(ticket.TerminalId), NameOrNull(ticket.SettledByUserId), ticket.SettledAt,
            ticket.SettledTerminalId is { } st ? Terminal(st) : null, period.BusinessDate, period.OpenedAt,
            counted.Sum(l => l.Payable), counted.Sum(l => l.Discount), Tickets.TicketPrints.DiscountLabel(ticket), ticket.RoundingAmount,
            ticket.MergedIntoTicketId is { } m ? await db.Tickets.AsNoTracking().Where(k => k.Id == m).Select(k => (long?)k.Number).FirstOrDefaultAsync(ct) : null,
            ticket.MergedIntoTicketId, mergedIn.Select(k => k.Number).ToList(), ticket.Note);
        return new TicketReportDto(header, lineDtos, printDtos, paymentDtos, approvalDtos, movementDtos, journal,
            timeline.OrderBy(e => e.At).ToList());
    }

    /// <summary>The whole ticket detail on A4: header, lines, prints with their slip text, payments, requests, stock, ledger and the timeline.</summary>
    public async Task<ReportFile> Pdf(Guid id, CancellationToken ct)
    {
        var d = await Execute(id, ct);
        var business = (await Settings.SettingsRegistry.Load(db, ct)).String(Settings.SettingsRegistry.BusinessName);
        var zone = await BusinessDates.Zone(db, ct);
        var t = d.Ticket;
        var facts = new List<string>
        {
            $"{t.Status} · table {t.Table ?? "-"} · opened by {t.OpenedBy} on {t.OpenedOn}",
            t.SettledAt is not null ? $"Settled by {t.SettledBy} on {t.SettledOn} · receipt {t.ReceiptNumber}" : "Not settled",
            $"Total KSh {ReportFormat.Money(t.Total)} · discount KSh {ReportFormat.Money(t.Discount)} · rounding KSh {ReportFormat.Money(t.Rounding)}",
        };
        ReportTable Table(string title, IReadOnlyList<ReportColumn> columns, IEnumerable<ReportRow> rows) =>
            new(title, columns, [new ReportGroup(null, rows.ToList())]);
        var tables = new List<ReportTable>
        {
            Table("Lines",
                [new("time", "Added", ColumnType.DateTime), new("item", "Item"), new("qty", "Qty", ColumnType.Quantity), new("amount", "Amount", ColumnType.Money),
                    new("status", "Status"), new("by", "Added by"), new("terminal", "Terminal"), new("round", "Round", ColumnType.Integer)],
                d.Lines.Select(l => ReportRow.Of(l.AddedAt, l.Name + (l.Options is null ? "" : $" ({l.Options})"), l.Quantity, l.Amount,
                    l.Status + (l.VoidReason is null ? "" : $": {l.VoidReason}"), l.AddedBy, l.Terminal, l.PrintRound))),
            Table("Prints",
                [new("time", "Time", ColumnType.DateTime), new("label", "Slip"), new("printer", "Printer"), new("by", "By"), new("status", "Status"),
                    new("text", "What it printed")],
                d.Prints.Select(p => ReportRow.Of(p.At, p.Label, p.Printer, p.By, p.Status, p.Text ?? "(slip text no longer kept)"))),
            Table("Payments",
                [new("time", "Time", ColumnType.DateTime), new("type", "Type"), new("amount", "Amount", ColumnType.Money), new("ref", "Code"),
                    new("taken", "Taken by"), new("brought", "Brought by"), new("status", "Status")],
                d.Payments.Select(p => ReportRow.Of(p.At, p.Type, p.Amount, p.Reference, p.TakenBy, p.BroughtBy, p.Status))),
            Table("Timeline",
                [new("time", "Time", ColumnType.DateTime), new("kind", "What"), new("text", "Detail"), new("by", "Who")],
                d.Timeline.Select(e => ReportRow.Of(e.At, e.Kind, e.Text, e.By))),
        };
        if (d.Approvals.Count > 0)
            tables.Insert(3, Table("Requests", [new("time", "Asked", ColumnType.DateTime), new("summary", "What"), new("by", "Asked by"),
                    new("status", "Status"), new("decided", "Decided by")],
                d.Approvals.Select(a => ReportRow.Of(a.RequestedAt, a.Summary, a.RequestedBy, a.Status, a.DecidedBy))));
        var result = new ReportResult("ticket", $"Ticket #{t.Number}", t.BusinessDate?.ToString("ddd d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            facts, DateTimeOffset.UtcNow, tables, []);
        return new ReportFile(ReportPdf.Build(result, business, zone), "application/pdf", $"ticket-{t.Number}.pdf");
    }

    public static string LineStatus(OrderLine l) => l.Status switch
    {
        OrderLineStatus.Cancelled => "Cancelled",
        _ when l.IsVoided => "Voided",
        _ when l.IsGift => "Gift",
        OrderLineStatus.New => "New",
        _ => "Printed",
    };

    private static string Qty(decimal quantity) => quantity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    public static async Task RequireReports(ICurrentSession session, IPermissionService permissions, CancellationToken ct)
    {
        if (await permissions.Check(session.RequireUser(), PermissionKeys.ReportsAll, ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(PermissionKeys.ReportsAll);
    }
}
