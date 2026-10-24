using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.Domain.Menu;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Reports;

/// <summary>
/// Voids, gifts and approvals: every void, gift, discount, on-ticket price change, reprint, move
/// and merge on the period's tickets (who asked, who approved, terminal, and for voids whether it
/// was poured), the requests that were rejected or expired, and totals by kind and by approver.
/// </summary>
public sealed class VoidsReport : ReportBase
{
    public static readonly IReadOnlyList<string> Kinds = ["Void", "Gift", "Discount", "Price change", "Reprint", "Move", "Merge"];

    public override ReportDefinition Definition { get; } = new("voids", "Voids, gifts and approvals", PermissionKeys.ReportsAll,
        [ReportFilter.Kind, ReportFilter.Requester, ReportFilter.Approver], Kinds: Kinds);

    private sealed record Event(
        DateTimeOffset At, string Kind, Guid TicketId, string Item, decimal? Quantity, decimal? Amount, string? Reason,
        Guid AskedBy, Guid? ApprovedBy, Guid? Terminal, bool? Poured);

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var f = c.Filters;
        var ids = c.Period.PeriodIds;
        var tickets = await c.Db.Tickets.AsNoTracking().Where(k => ids.Contains(k.WorkPeriodId))
            .ToDictionaryAsync(k => k.Id, k => k.Number, ct);
        var inPeriod = c.Db.Tickets.AsNoTracking().Where(k => ids.Contains(k.WorkPeriodId)).Select(k => k.Id);

        var events = new List<Event>();
        events.AddRange((await c.Db.VoidLogs.AsNoTracking().Where(v => inPeriod.Contains(v.TicketId)).ToListAsync(ct))
            .Select(v => new Event(v.At, "Void", v.TicketId, v.ItemName, v.Quantity, v.Amount, Join(v.Reason, v.Note), v.RequestedByUserId,
                v.ApprovedByUserId, v.TerminalId, v.WasPoured)));
        events.AddRange((await c.Db.GiftLogs.AsNoTracking().Where(g => inPeriod.Contains(g.TicketId)).ToListAsync(ct))
            .Select(g => new Event(g.At, "Gift", g.TicketId, g.ItemName, g.Quantity, g.Amount, Join(g.Reason, g.Note), g.RequestedByUserId,
                g.ApprovedByUserId, g.TerminalId, null)));
        events.AddRange((await c.Db.DiscountLogs.AsNoTracking().Where(d => inPeriod.Contains(d.TicketId)).ToListAsync(ct))
            .Select(d => new Event(d.At, "Discount", d.TicketId,
                d.Kind is null ? "Discount removed" : d.Kind == DiscountKind.Percent ? $"Discount {d.Value:0.##}%" : "Discount", null, d.Amount,
                Join(d.Reason, d.Note), d.RequestedByUserId, d.ApprovedByUserId, d.TerminalId, null)));
        var priceChanges = await (from p in c.Db.PriceChangeLogs.AsNoTracking()
                                  join l in c.Db.OrderLines.AsNoTracking() on p.OrderLineId equals l.Id
                                  where p.Kind == PriceChangeKind.Ticket && inPeriod.Contains(p.TicketId!.Value)
                                  select new { p.At, p.TicketId, l.Name, l.Quantity, p.OldPrice, p.NewPrice, p.Reason, p.ChangedByUserId, p.ApprovedByUserId, p.TerminalId })
            .ToListAsync(ct);
        events.AddRange(priceChanges.Select(p => new Event(p.At, "Price change", p.TicketId!.Value,
            $"{p.Name}: {ReportFormat.Money(p.OldPrice ?? 0)} to {ReportFormat.Money(p.NewPrice ?? 0)}", p.Quantity,
            ((p.NewPrice ?? 0) - (p.OldPrice ?? 0)) * p.Quantity, p.Reason, p.ChangedByUserId, p.ApprovedByUserId, p.TerminalId, null)));

        string[] actions = [AuditActions.BillReprinted, AuditActions.ReceiptReprinted, AuditActions.TicketMoved, AuditActions.TicketMerged];
        var audits = await c.Db.AuditEntries.AsNoTracking()
            .Where(a => actions.Contains(a.Action) && a.At >= c.Period.Start && a.At <= c.Period.End).ToListAsync(ct);
        foreach (var a in audits)
        {
            if (!Guid.TryParse(a.EntityId, out var ticket) || !tickets.ContainsKey(ticket) || a.UserId is not { } user) continue;
            var (kind, item) = a.Action switch
            {
                AuditActions.BillReprinted => ("Reprint", "Bill"),
                AuditActions.ReceiptReprinted => ("Reprint", "Receipt"),
                AuditActions.TicketMoved => ("Move", $"To {Read(a.NewValueJson, "table") ?? "another table"}"),
                _ => ("Merge", $"Merged in {Read(a.NewValueJson, "merged") ?? "tickets"}"),
            };
            events.Add(new Event(a.At, kind, ticket, item, null, null, null, user, a.ApprovedByUserId, a.TerminalId, null));
        }

        var shown = events
            .Where(e => string.IsNullOrEmpty(f.Kind) || e.Kind == f.Kind)
            .Where(e => f.RequesterId is null || e.AskedBy == f.RequesterId)
            .Where(e => f.ApproverId is null || e.ApprovedBy == f.ApproverId)
            .OrderBy(e => e.At).ToList();

        var eventTable = new ReportTable("Events",
        [
            new("time", "Time", ColumnType.DateTime), new("kind", "Kind"), new("ticket", "Ticket", ColumnType.Ticket), new("item", "Item"),
            new("qty", "Qty", ColumnType.Quantity), new("amount", "Amount", ColumnType.Money), new("reason", "Reason and note"),
            new("asked", "Asked by"), new("approved", "Approved by"), new("terminal", "Terminal"), new("poured", "Poured"),
        ],
        [new ReportGroup(null, shown.Select(e => new ReportRow([e.At, e.Kind, tickets.GetValueOrDefault(e.TicketId), e.Item, e.Quantity, e.Amount,
            e.Reason, c.UserName(e.AskedBy), c.UserName(e.ApprovedBy), c.TerminalName(e.Terminal), e.Poured is { } p ? (p ? "Poured" : "Not poured") : null],
            e.TicketId)).ToList())]);

        var byKind = new ReportTable("Totals by kind",
            [new("kind", "Kind"), new("count", "Count", ColumnType.Integer), new("amount", "Amount", ColumnType.Money)],
            [new ReportGroup(null, shown.GroupBy(e => e.Kind).OrderBy(g => Array.IndexOf(Kinds.ToArray(), g.Key))
                .Select(g => ReportRow.Of(g.Key, g.Count(), g.Sum(e => e.Amount ?? 0))).ToList())]);
        var byApprover = new ReportTable("Totals by approver",
            [new("approver", "Approved by"), new("count", "Count", ColumnType.Integer), new("amount", "Amount", ColumnType.Money)],
            [new ReportGroup(null, shown.Where(e => e.ApprovedBy is not null).GroupBy(e => e.ApprovedBy!.Value)
                .OrderByDescending(g => g.Count()).ThenBy(g => c.UserName(g.Key))
                .Select(g => ReportRow.Of(c.UserName(g.Key), g.Count(), g.Sum(e => e.Amount ?? 0))).ToList())]);

        // Requests that didn't go through: rejected, expired or cancelled.
        var requests = await c.Db.ApprovalRequests.AsNoTracking()
            .Where(r => ids.Contains(r.WorkPeriodId)
                        && (r.Status == ApprovalStatus.Rejected || r.Status == ApprovalStatus.Expired || r.Status == ApprovalStatus.Cancelled))
            .OrderBy(r => r.RequestedAt).ToListAsync(ct);
        var requestRows = requests
            .Where(r => string.IsNullOrEmpty(f.Kind) || KindName(r.Kind) == f.Kind)
            .Where(r => f.RequesterId is null || r.RequestedByUserId == f.RequesterId)
            .Where(r => f.ApproverId is null || r.DecidedByUserId == f.ApproverId)
            .Select(r => new ReportRow([r.RequestedAt, KindName(r.Kind), tickets.GetValueOrDefault(r.TicketId), r.Summary, r.Amount,
                c.UserName(r.RequestedByUserId), r.Status.ToString(), c.UserName(r.DecidedByUserId), Join(r.Reason, r.DecisionNote)], r.TicketId))
            .ToList();
        var requestTable = new ReportTable("Rejected and expired requests",
        [
            new("time", "Asked at", ColumnType.DateTime), new("kind", "Kind"), new("ticket", "Ticket", ColumnType.Ticket), new("what", "What"),
            new("amount", "Amount", ColumnType.Money), new("asked", "Asked by"), new("status", "Status"), new("decided", "Decided by"),
            new("note", "Reason and note"),
        ],
        [new ReportGroup(null, requestRows)]);

        return await Result(c, [eventTable, byKind, byApprover, requestTable], ct);
    }

    private static string KindName(ApprovalKind kind) => kind == ApprovalKind.PriceChange ? "Price change" : kind.ToString();

    private static string? Join(string? a, string? b) =>
        string.Join(" · ", new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } s ? s : null;

    private static string? Read(string? json, string property)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(property, out var value)) return null;
            return value.ValueKind == JsonValueKind.Array
                ? string.Join(", ", value.EnumerateArray().Select(v => "#" + v))
                : value.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
