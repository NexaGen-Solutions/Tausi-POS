namespace Tausi.Application.Auditing;

public static class AuditActions
{
    public const string PinLogin = "auth.pin_login";
    public const string PinLoginFailed = "auth.pin_failed";
    public const string TerminalLocked = "auth.terminal_locked";
    public const string Logout = "auth.logout";
    public const string PinChanged = "auth.pin_changed";
    public const string PinChangeRequired = "auth.pin_change_required";
    public const string AdminLogin = "auth.admin_login";
    public const string AdminLoginFailed = "auth.admin_failed";
    public const string ApprovalIssued = "auth.approval_issued";
    public const string TerminalPaired = "terminal.paired";
    public const string PermissionChanged = "permissions.changed";
    public const string SettingsChanged = "settings.changed";
    public const string LogoChanged = "settings.logo_changed";
    public const string TestPrint = "printer.test";
    public const string DayOpened = "day.opened";
    public const string MenuCategoryChanged = "menu.category_changed";
    public const string MenuItemChanged = "menu.item_changed";
    public const string MenuPricesChanged = "menu.prices_changed";
    public const string MenuGroupChanged = "menu.group_changed";
    public const string MenuPriceListChanged = "menu.price_list_changed";
    public const string MenuImported = "menu.imported";
    public const string TerminalPriceList = "terminal.price_list";
    public const string StaffCreated = "staff.created";
    public const string StaffUpdated = "staff.updated";
    public const string StaffPinReset = "staff.pin_reset";
    public const string StaffDeactivated = "staff.deactivated";
    public const string StaffReactivated = "staff.reactivated";
    public const string TicketMoved = "ticket.moved";
    public const string TicketMerged = "ticket.merged";
    public const string TicketPrinted = "ticket.printed";
    public const string BillReprinted = "ticket.bill_reprinted";
    public const string LineCancelled = "order.cancelled_unprinted";
    public const string PrintJobRetried = "print.job_retried";
    public const string AreaChanged = "tables.area_changed";
    public const string TableChanged = "tables.table_changed";
    public const string TablesAdded = "tables.added";
    public const string LineVoided = "order.voided";
    public const string LineGifted = "order.gifted";
    public const string DiscountChanged = "order.discount";
    public const string LinePriceChanged = "order.price_changed";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalApproved = "approval.approved";
    public const string ApprovalRejected = "approval.rejected";
    public const string ApprovalCancelled = "approval.cancelled";
    public const string ApprovalExpired = "approval.expired";
}

/// <summary>
/// One audit entry. User and terminal default to the current session; pass them explicitly when
/// there is no session yet (e.g. a failed login). Old and new values are serialised to JSON.
/// </summary>
public sealed record AuditRecord(
    string Action,
    string? EntityType = null,
    string? EntityId = null,
    object? OldValue = null,
    object? NewValue = null,
    Guid? ApprovedByUserId = null,
    Guid? UserId = null,
    Guid? TerminalId = null);

public interface IAuditLog
{
    /// <summary>
    /// Adds the entry to the current unit of work. It is saved with the caller's
    /// <c>SaveChangesAsync</c>, so the audit and the change it describes commit together.
    /// </summary>
    void Write(AuditRecord record);
}
