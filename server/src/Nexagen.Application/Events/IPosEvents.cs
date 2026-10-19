namespace Nexagen.Application.Events;

/// <summary>Live-update event names (<c>noun.verb</c>). Payloads are small; clients refetch details.</summary>
public static class PosEventNames
{
    public const string TableUpdated = "table.updated";
    public const string TicketUpdated = "ticket.updated";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalResolved = "approval.resolved";
    public const string PrintFailed = "print.failed";
    public const string PrinterStatus = "printer.status";
    public const string MenuChanged = "menu.changed";
    public const string SettingsChanged = "settings.changed";
    public const string DayOpened = "day.opened";
}

public interface IPosEvents
{
    /// <summary>Sends an event to every connected client of the business.</summary>
    Task Publish(string eventName, object payload, CancellationToken ct = default);
}
