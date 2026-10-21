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
    int NewLines = 0);

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
    bool Voided);

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
    IReadOnlyList<FailedJobDto>? FailedJobs = null);
