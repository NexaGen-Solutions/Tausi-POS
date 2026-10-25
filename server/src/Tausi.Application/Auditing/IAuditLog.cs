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
    public const string DayClosed = "day.closed";
    public const string ShiftOpened = "shift.opened";
    public const string ShiftCounted = "shift.counted";
    public const string ShiftClosed = "shift.closed";
    public const string PayOut = "shift.pay_out";
    public const string CashDrop = "shift.cash_drop";
    public const string PaymentAdded = "payment.added";
    public const string PaymentRemoved = "payment.removed";
    public const string TicketSettled = "ticket.settled";
    public const string ReceiptReprinted = "ticket.receipt_reprinted";
    public const string TicketClosedEmpty = "ticket.closed_empty";
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
    public const string TicketClosedVoided = "ticket.closed_voided";
    public const string RoleCreated = "permissions.role_created";
    public const string RoleRenamed = "permissions.role_renamed";
    public const string RoleDeleted = "permissions.role_deleted";
    public const string StockItemChanged = "stock.item_changed";
    public const string RecipeChanged = "stock.recipe_changed";
    public const string SupplierChanged = "stock.supplier_changed";
    public const string DeliveryRecorded = "stock.delivery";
    public const string DeliveryReversed = "stock.delivery_reversed";
    public const string WasteRecorded = "stock.waste";
    public const string TransferRecorded = "stock.transfer";
    public const string StockCountChanged = "stock.count";
    public const string StockDocumentApproved = "stock.approved";
    public const string StockDocumentRejected = "stock.rejected";
    public const string MoneyRecorded = "money.recorded";
    public const string MoneyApproved = "money.approved";
    public const string MoneyRejected = "money.rejected";
    public const string MoneyReversed = "money.reversed";
    public const string ExpenseCategoryChanged = "money.category_changed";
    public const string SetupCompleted = "setup.completed";
    public const string SetupStaffImported = "setup.staff_imported";
    public const string LicenceEntered = "licence.entered";
    public const string LicenceTrialStarted = "licence.trial_started";
    public const string StockLocationChanged = "stock.location_changed";
}

/// <summary>Entity types of audit entries that more than one place writes or reads.</summary>
public static class AuditEntities
{
    /// <summary>One setting; the entity id is its key, e.g. "pos.idleLogoutMinutes".</summary>
    public const string Setting = "Setting";

    public const string Role = "Role";
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
    Guid? TerminalId = null,
    string? Actor = null);

public interface IAuditLog
{
    /// <summary>
    /// Adds the entry to the current unit of work. It is saved with the caller's
    /// <c>SaveChangesAsync</c>, so the audit and the change it describes commit together.
    /// </summary>
    void Write(AuditRecord record);
}
