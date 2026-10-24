using Tausi.Domain.Service;

namespace Tausi.Application.Reports;

/// <summary>Who a sale counts for: who added the line (the default) or who opened the ticket.</summary>
public enum CreditTo
{
    AddedBy,
    OpenedBy,
}

/// <summary>Quick picks for a range of business dates.</summary>
public enum QuickRange
{
    Today,
    Yesterday,
    ThisWeek,
    LastWeek,
    ThisMonth,
    LastMonth,
}

/// <summary>
/// Every filter any report takes; each report says which ones it uses (<see cref="ReportFilter"/>).
/// The period is a work period (default: the open one, else the last closed), or a range of
/// business dates (<see cref="From"/>/<see cref="To"/> or a <see cref="Quick"/> pick).
/// </summary>
public sealed record ReportFilters
{
    public Guid? WorkPeriodId { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public QuickRange? Quick { get; init; }

    public Guid? UserId { get; init; }
    public Guid? TerminalId { get; init; }
    public Guid? LocationId { get; init; }
    public Guid? CategoryId { get; init; }
    public PaymentType? PaymentType { get; init; }
    public Guid? SupplierId { get; init; }
    public CreditTo CreditTo { get; init; } = CreditTo.AddedBy;
    /// <summary>Null: on for the current work period, off (and hidden) for closed ones.</summary>
    public bool? IncludeOpen { get; init; }

    /// <summary>Payments and cash-ups: the cashier and the waiter (who brought the money).</summary>
    public Guid? CashierId { get; init; }
    public Guid? WaiterId { get; init; }

    /// <summary>Voids and approvals, price changes: the kind of event ("Void", "Gift", "Menu", "Ticket" …).</summary>
    public string? Kind { get; init; }
    public Guid? RequesterId { get; init; }
    public Guid? ApproverId { get; init; }

    /// <summary>Stock on hand: worked out from movements up to the end of this date.</summary>
    public DateOnly? AsAt { get; init; }
    public bool OnlyBelowReorder { get; init; }
    public Domain.Stock.StockCategory? StockCategory { get; init; }

    /// <summary>Stock variance: the count (default: the last approved one), or one item's trend.</summary>
    public Guid? CountId { get; init; }
    public Guid? StockItemId { get; init; }

    /// <summary>Tickets and receipts.</summary>
    public TicketStatus? Status { get; init; }
    public Guid? TableId { get; init; }
    public decimal? MinAmount { get; init; }
    public decimal? MaxAmount { get; init; }
    public bool HasVoids { get; init; }
    public bool HasReprints { get; init; }
    public bool HasDiscount { get; init; }

    /// <summary>Users, roles and terminals.</summary>
    public Guid? RoleId { get; init; }

    /// <summary>Audit log.</summary>
    public string? Action { get; init; }
    public string? Entity { get; init; }
    public string? Search { get; init; }
}

/// <summary>The filter controls a report shows (the period is shown unless <see cref="ReportDefinition.UsesPeriod"/> is false).</summary>
public static class ReportFilter
{
    public const string User = "user";
    public const string Terminal = "terminal";
    public const string Location = "location";
    public const string Category = "category";
    public const string PaymentType = "paymentType";
    public const string Supplier = "supplier";
    public const string CreditTo = "creditTo";
    public const string IncludeOpen = "includeOpen";
    public const string Cashier = "cashier";
    public const string Waiter = "waiter";
    public const string Kind = "kind";
    public const string Requester = "requester";
    public const string Approver = "approver";
    public const string AsAt = "asAt";
    public const string OnlyBelowReorder = "onlyBelowReorder";
    public const string StockCategory = "stockCategory";
    public const string Count = "count";
    public const string StockItem = "stockItem";
    public const string Status = "status";
    public const string Table = "table";
    public const string Amount = "amount";
    public const string Flags = "flags";
    public const string Role = "role";
    public const string Action = "action";
    public const string Entity = "entity";
    public const string Search = "search";
}

/// <summary>How a column's values are shown, printed and exported.</summary>
public enum ColumnType
{
    Text,
    Money,
    Quantity,
    Percent,
    Integer,
    Date,
    DateTime,
    Time,
    /// <summary>A ticket number; the row's <see cref="ReportRow.TicketId"/> makes it a link.</summary>
    Ticket,
}

public sealed record ReportColumn(string Key, string Label, ColumnType Type = ColumnType.Text)
{
    public bool RightAligned => Type is ColumnType.Money or ColumnType.Quantity or ColumnType.Percent or ColumnType.Integer;
}

/// <summary>
/// One row: a value per column (null for empty), and the ticket it is about, if any.
/// Values are strings, decimals, ints, longs, DateOnly or DateTimeOffset.
/// </summary>
public sealed record ReportRow(IReadOnlyList<object?> Cells, Guid? TicketId = null, bool Muted = false)
{
    public static ReportRow Of(params object?[] cells) => new(cells);
}

/// <summary>
/// A group: its header row (the title, an optional subtitle such as the role, and the group's
/// total on the right), the rows, an optional subtotal row and footer lines ("Tickets 12 · Gifts 400").
/// An ungrouped table has one group without a title.
/// </summary>
public sealed record ReportGroup(
    string? Title, IReadOnlyList<ReportRow> Rows, ReportRow? Subtotal = null, IReadOnlyList<string>? Footer = null,
    string? Subtitle = null, decimal? Total = null);

/// <summary>One table of a report (some reports have several: per shift, per waiter, M-Pesa codes).</summary>
public sealed record ReportTable(
    string? Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<ReportGroup> Groups, ReportRow? Totals = null,
    IReadOnlyList<string>? Footer = null);

/// <summary>A report's result, the same for the screen, PDF, Excel, CSV and the receipt printer.</summary>
public sealed record ReportResult(
    string Key, string Title, string Period, IReadOnlyList<string> FilterText, DateTimeOffset RunAt,
    IReadOnlyList<ReportTable> Tables, IReadOnlyList<string> Notes)
{
    public bool IsEmpty => Tables.All(t => t.Groups.All(g => g.Rows.Count == 0));
}

/// <summary>A report in the list: what it is, what it filters on, and whether it prints on a receipt printer.</summary>
public sealed record ReportDefinition(
    string Key, string Title, string Permission, IReadOnlyList<string> Filters, bool UsesPeriod = true, bool Receipt = false,
    IReadOnlyList<string>? Kinds = null);

/// <summary>One report: its definition and how to run it.</summary>
public interface IReport
{
    ReportDefinition Definition { get; }

    Task<ReportResult> Run(ReportContext context, CancellationToken ct);
}
