using Nexagen.Domain.People;
using static Nexagen.Domain.People.PermissionKeys;
using static Nexagen.Domain.People.PermissionValue;

namespace Nexagen.Infrastructure.Seeding;

/// <summary>The default role x permission grid for a new business (from the foundation task).</summary>
public static class DefaultPermissions
{
    public const string Waiter = "Waiter";
    public const string Cashier = "Cashier";
    public const string Supervisor = "Supervisor";
    public const string Admin = "Admin";

    /// <summary>Role name, whether it may use the admin site.</summary>
    public static readonly IReadOnlyList<(string Name, bool CanUseAdminSite)> Roles =
    [
        (Waiter, false),
        (Cashier, false),
        (Supervisor, true),
        (Admin, true),
    ];

    private static readonly (string[] Keys, PermissionValue Waiter, PermissionValue Cashier, PermissionValue Supervisor, PermissionValue Admin)[] Rows =
    [
        ([OrderAdd, OrderCancelUnprinted, TicketPrint, TicketMove, TicketMerge, ReportsOwn], Allowed, Allowed, Allowed, Allowed),
        ([OrderVoid], NeedsApproval, NeedsApproval, Allowed, Allowed),
        ([OrderGift, OrderDiscount], NotAllowed, NeedsApproval, Allowed, Allowed),
        ([TicketReprint], NeedsApproval, Allowed, Allowed, Allowed),
        ([OrderChangePrice, StockCount, StockAdjust], NotAllowed, NotAllowed, NeedsApproval, Allowed),
        ([TicketSettle], NotAllowed, Allowed, Allowed, Allowed),
        ([CashExpense], NotAllowed, Allowed, Allowed, Allowed),
        ([StockReceive, StockWaste, CashBook, ReportsAll, DayOpenClose, StaffManage], NotAllowed, NotAllowed, Allowed, Allowed),
        ([MenuEdit, MenuEditPrices, PermissionsManage, DevicesManage, SettingsManage, ErpManage], NotAllowed, NotAllowed, NotAllowed, Allowed),
    ];

    public static PermissionValue For(string role, string key)
    {
        foreach (var row in Rows)
        {
            if (!row.Keys.Contains(key)) continue;
            return role switch
            {
                Waiter => row.Waiter,
                Cashier => row.Cashier,
                Supervisor => row.Supervisor,
                Admin => row.Admin,
                _ => NotAllowed,
            };
        }
        return NotAllowed;
    }

    public static IEnumerable<string> CoveredKeys => Rows.SelectMany(r => r.Keys);
}
