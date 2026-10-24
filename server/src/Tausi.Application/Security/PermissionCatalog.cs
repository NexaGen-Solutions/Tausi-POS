using Tausi.Domain.People;
using static Tausi.Domain.People.PermissionKeys;

namespace Tausi.Application.Security;

/// <summary>One row of the permissions grid: its section, a plain label and a help line.</summary>
public sealed record PermissionInfo(string Key, string Section, string Label, string Help, bool Approvable);

/// <summary>
/// Every permission key from AGENTS.md as the permissions screen shows it, in its order. Keys that
/// can't be approved (<see cref="NotApprovable"/>) are only Allowed or Not allowed: a supervisor can't
/// lend them with a PIN, and a NeedsApproval cell for one counts as Not allowed.
/// </summary>
public static class PermissionCatalog
{
    private const string NeedsApprovalHelp = "Needs approval: supervisor PIN or request.";

    public static readonly IReadOnlyList<PermissionInfo> All =
    [
        new(OrderAdd, "Orders", "Add and send orders", "Add items to a ticket and send them to the bar.", true),
        new(OrderCancelUnprinted, "Orders", "Cancel unprinted items", "Take an item off before it is printed.", true),
        new(OrderVoid, "Orders", "Void a printed item", $"Take off an item already sent. {NeedsApprovalHelp}", true),
        new(OrderGift, "Orders", "Gift an item", $"Give an item free; it still counts as sold. {NeedsApprovalHelp}", true),
        new(OrderDiscount, "Orders", "Discount a ticket", $"A percentage or an amount off the bill. {NeedsApprovalHelp}", true),
        new(OrderChangePrice, "Orders", "Change a price on a ticket", $"For one ticket only; the menu price stays. {NeedsApprovalHelp}", true),

        new(TicketPrint, "Tickets", "Print bills and send orders", "The Print button, or Send and Print bill.", true),
        new(TicketReprint, "Tickets", "Reprint the bill", $"Prints the bill again, marked REPRINT. {NeedsApprovalHelp}", true),
        new(TicketMove, "Tickets", "Move a ticket", "To another table.", true),
        new(TicketMerge, "Tickets", "Merge tickets", "Puts two tickets together on one table.", true),
        new(TicketSettle, "Tickets", "Settle and take payment", "Only cashiers settle; it can't be approved for someone else.", false),

        new(MenuEdit, "Menu", "Edit the menu", "Items, categories, options and price lists on the admin site.", false),
        new(MenuEditPrices, "Menu", "Edit menu prices", "Change prices on the admin site.", false),

        new(StockReceive, "Stock", "Receive deliveries", "Record what a supplier brought.", true),
        new(StockWaste, "Stock", "Record waste", "Spilled, broken or spoiled stock.", true),
        new(StockCount, "Stock", "Stock counts", $"Count stock and see the variance. {NeedsApprovalHelp}", true),
        new(StockAdjust, "Stock", "Stock adjustments", $"Correct a stock level by hand. {NeedsApprovalHelp}", true),

        new(CashExpense, "Money", "Expenses", $"Pay out from the drawer for supplies, transport and the like. {NeedsApprovalHelp}", true),
        new(CashBook, "Money", "Cash book", "The cash book and the daily summary on the admin site.", true),

        new(ReportsOwn, "Reports", "Own sales", "My sales on the terminal.", false),
        new(ReportsAll, "Reports", "All reports", "Everyone's sales and every report.", false),

        new(DayOpenClose, "Day", "Open and close the day", "Also signs off cashiers' cash-ups.", false),

        new(StaffManage, "Admin", "Staff and PINs", "Add staff, set PINs, deactivate.", false),
        new(PermissionsManage, "Admin", "Permissions", "This screen.", false),
        new(DevicesManage, "Admin", "Terminals and printers", "Pair terminals, set up printers.", false),
        new(SettingsManage, "Admin", "Settings", "Business details and rules.", false),
        new(ErpManage, "Admin", "ERPNext sync", "The connection to ERPNext.", false),
    ];

    public static readonly IReadOnlySet<string> NotApprovable = All.Where(p => !p.Approvable).Select(p => p.Key).ToHashSet();

    private static readonly Dictionary<string, PermissionInfo> ByKey = All.ToDictionary(p => p.Key);

    public static PermissionInfo? Find(string key) => ByKey.GetValueOrDefault(key);

    public static bool IsApprovable(string key) => !NotApprovable.Contains(key);

    /// <summary>The Admin role keeps permissions.manage, so nobody can lock everyone out.</summary>
    public static bool IsLocked(string? roleSystemKey, string key) => roleSystemKey == RoleKeys.Admin && key == PermissionsManage;
}
