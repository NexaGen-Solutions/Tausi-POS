namespace Tausi.Application.Tickets;

/// <summary>The terminal or user holding a ticket's edit lock.</summary>
public sealed record LockDto(Guid TerminalId, string Terminal, Guid UserId, string User);

/// <summary>An open ticket as the table map and the takeaway list show it.</summary>
public sealed record TicketSummaryDto(
    Guid Id,
    long Number,
    Guid? TableId,
    string? CustomerName,
    Guid OpenedById,
    string OpenedBy,
    decimal Total,
    DateTimeOffset OpenedAt,
    DateTimeOffset LastActivityAt,
    bool Mine,
    LockDto? Lock,
    int NewLines = 0,
    decimal Paid = 0,
    decimal? Left = null);

public sealed record AreaDto(Guid Id, string Name, int SortOrder);

public sealed record TableTileDto(
    Guid Id,
    string Name,
    Guid AreaId,
    int SortOrder,
    IReadOnlyList<TicketSummaryDto> Tickets,
    int? IdleMinutes);

/// <summary>
/// Everything the table map draws. <see cref="Version"/> lets a terminal spot a missed event;
/// <see cref="ServerTime"/> lets it count idle minutes on its own clock.
/// </summary>
public sealed record TableMapDto(
    long Version,
    DateTimeOffset ServerTime,
    bool DayOpen,
    int TableIdleMinutes,
    IReadOnlyList<AreaDto> Areas,
    IReadOnlyList<TableTileDto> Tables);

public sealed record OrderLineDto(
    Guid Id,
    Guid? MenuItemId,
    string Name,
    string ShortName,
    string? Options,
    string? Note,
    decimal Quantity,
    decimal UnitPrice,
    decimal Gross,
    string Status,
    int? PrintRound,
    Guid AddedById,
    string AddedBy,
    string AddedOn,
    DateTimeOffset AddedAt,
    DateTimeOffset? PrintedAt,
    bool Voided,
    bool IsGift = false,
    decimal? OriginalUnitPrice = null,
    decimal Discount = 0,
    string? VoidReason = null,
    PendingRequestDto? Pending = null);

/// <summary>A request waiting for approval: "Void pending" on its line, or "Discount pending" on the ticket.</summary>
public sealed record PendingRequestDto(Guid Id, string Kind, string Summary, string RequestedBy, bool Mine);

/// <summary>A print job for this ticket that gave up (the order screen offers Retry).</summary>
public sealed record FailedJobDto(Guid Id, string Kind, string PrinterName, string? Error);

public sealed record TicketDetailDto(
    Guid Id,
    long Number,
    string Status,
    Guid? TableId,
    string? TableName,
    string? CustomerName,
    string OpenedBy,
    DateTimeOffset OpenedAt,
    DateTimeOffset LastActivityAt,
    string Note,
    decimal Total,
    IReadOnlyList<OrderLineDto> Lines,
    LockDto? Lock,
    long RowVersion,
    int BillNumber = 0,
    int NewLines = 0,
    IReadOnlyList<FailedJobDto>? FailedJobs = null,
    decimal Subtotal = 0,
    decimal DiscountTotal = 0,
    string? DiscountLabel = null,
    IReadOnlyList<PendingRequestDto>? Pending = null,
    IReadOnlyList<PaymentDto>? Payments = null,
    decimal AmountDue = 0,
    decimal Rounding = 0,
    decimal Remaining = 0,
    long? ReceiptNumber = null,
    Guid? DefaultBroughtById = null,
    IReadOnlyList<VatLineDto>? VatLines = null);

/// <summary>"Total includes VAT 16%" with its amount, or "VAT exempt" with the exempt amount, as on the bill.</summary>
public sealed record VatLineDto(string Label, decimal Amount);

/// <summary>A payment on the settle screen: "M-Pesa SJK3XXXXXX − 1,000", "Cash (edna) − 370".</summary>
public sealed record PaymentDto(
    Guid Id, string Type, decimal Amount, decimal? Tendered, decimal? Change, string? MpesaCode, string? CardReference,
    Guid BroughtById, string BroughtBy, string TakenBy, string Status, DateTimeOffset TakenAt);
