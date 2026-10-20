namespace Tausi.Domain.People;

/// <summary>The permission keys from AGENTS.md. Use these constants, never string literals.</summary>
public static class PermissionKeys
{
    public const string OrderAdd = "order.add";
    public const string OrderCancelUnprinted = "order.cancel_unprinted";
    public const string OrderVoid = "order.void";
    public const string OrderGift = "order.gift";
    public const string OrderDiscount = "order.discount";
    public const string OrderChangePrice = "order.change_price";
    public const string TicketPrint = "ticket.print";
    public const string TicketReprint = "ticket.reprint";
    public const string TicketMove = "ticket.move";
    public const string TicketMerge = "ticket.merge";
    public const string TicketSettle = "ticket.settle";
    public const string MenuEdit = "menu.edit";
    public const string MenuEditPrices = "menu.edit_prices";
    public const string StockReceive = "stock.receive";
    public const string StockWaste = "stock.waste";
    public const string StockCount = "stock.count";
    public const string StockAdjust = "stock.adjust";
    public const string CashExpense = "cash.expense";
    public const string CashBook = "cash.book";
    public const string ReportsOwn = "reports.own";
    public const string ReportsAll = "reports.all";
    public const string DayOpenClose = "day.open_close";
    public const string StaffManage = "staff.manage";
    public const string PermissionsManage = "permissions.manage";
    public const string DevicesManage = "devices.manage";
    public const string SettingsManage = "settings.manage";
    public const string ErpManage = "erp.manage";

    public static readonly IReadOnlyList<string> All =
    [
        OrderAdd, OrderCancelUnprinted, OrderVoid, OrderGift, OrderDiscount, OrderChangePrice,
        TicketPrint, TicketReprint, TicketMove, TicketMerge, TicketSettle,
        MenuEdit, MenuEditPrices,
        StockReceive, StockWaste, StockCount, StockAdjust,
        CashExpense, CashBook,
        ReportsOwn, ReportsAll, DayOpenClose,
        StaffManage, PermissionsManage, DevicesManage, SettingsManage, ErpManage,
    ];

    public static bool IsKnown(string key) => All.Contains(key);
}
