namespace Tausi.Application.Events;

/// <summary>Live-update event names (<c>noun.verb</c>). Payloads are small; clients refetch details.</summary>
public static class PosEventNames
{
    public const string TicketChanged = "ticket.changed";
    public const string TicketLock = "ticket.lock";
    public const string TablesChanged = "tables.changed";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalResolved = "approval.resolved";
    public const string PrintFailed = "print.failed";
    public const string PrinterStatus = "printer.status";
    public const string MenuChanged = "menu.changed";
    public const string SettingsChanged = "settings.changed";
    public const string PermissionsChanged = "permissions.changed";
    public const string DayOpened = "day.opened";
    public const string DayClosed = "day.closed";
    public const string UserDeactivated = "user.deactivated";
    public const string UserPinReset = "user.pin_reset";
    public const string TerminalStatus = "terminal.status";
    public const string TerminalRevoked = "terminal.revoked";
    public const string SetupCompleted = "setup.completed";
    public const string LicenceChanged = "licence.changed";
}

public interface IPosEvents
{
    /// <summary>Sends an event to every connected client of the business.</summary>
    Task Publish(string eventName, object payload, CancellationToken ct = default);
}
