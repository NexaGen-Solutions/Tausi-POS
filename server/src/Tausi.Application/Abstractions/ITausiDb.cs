using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Tausi.Domain.Audit;
using Tausi.Domain.Devices;
using Tausi.Domain.Ledger;
using Tausi.Domain.Menu;
using Tausi.Domain.People;
using Tausi.Domain.Platform;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;
using Tausi.Domain.Settings;

namespace Tausi.Application.Abstractions;

/// <summary>The database as the use cases see it. Implemented by the EF Core context in Infrastructure.</summary>
public interface ITausiDb
{
    DbSet<BusinessSettings> BusinessSettings { get; }
    DbSet<Licence> Licences { get; }
    DbSet<Role> Roles { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<User> Users { get; }
    DbSet<ApprovalGrant> ApprovalGrants { get; }
    DbSet<PinFailure> PinFailures { get; }
    DbSet<UserSession> UserSessions { get; }
    DbSet<WorkPeriod> WorkPeriods { get; }
    DbSet<Terminal> Terminals { get; }
    DbSet<TerminalPairing> TerminalPairings { get; }
    DbSet<Printer> Printers { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<StoredFile> StoredFiles { get; }
    DbSet<PrintJob> PrintJobs { get; }
    DbSet<LedgerAccount> LedgerAccounts { get; }
    DbSet<JournalEntry> JournalEntries { get; }
    DbSet<JournalLine> JournalLines { get; }
    DbSet<Category> Categories { get; }
    DbSet<MenuItem> MenuItems { get; }
    DbSet<MenuItemPrice> MenuItemPrices { get; }
    DbSet<PriceList> PriceLists { get; }
    DbSet<ModifierGroup> ModifierGroups { get; }
    DbSet<ModifierOption> ModifierOptions { get; }
    DbSet<ModifierGroupAssignment> ModifierGroupAssignments { get; }
    DbSet<PriceChangeLog> PriceChangeLogs { get; }
    DbSet<Area> Areas { get; }
    DbSet<DiningTable> Tables { get; }
    DbSet<Ticket> Tickets { get; }
    DbSet<OrderLine> OrderLines { get; }
    DbSet<ApprovalRequest> ApprovalRequests { get; }
    DbSet<VoidLog> VoidLogs { get; }
    DbSet<GiftLog> GiftLogs { get; }
    DbSet<DiscountLog> DiscountLogs { get; }
    DbSet<Payment> Payments { get; }
    DbSet<Domain.Cash.CashierShift> CashierShifts { get; }
    DbSet<Domain.Cash.DrawerMovement> DrawerMovements { get; }
    DbSet<Domain.Cash.ExpenseCategory> ExpenseCategories { get; }
    DbSet<Domain.Stock.StockLocation> StockLocations { get; }
    DbSet<Domain.Stock.StockItem> StockItems { get; }
    DbSet<Domain.Stock.StockLevel> StockLevels { get; }
    DbSet<Domain.Stock.StockMovement> StockMovements { get; }
    DbSet<Domain.Stock.RecipeLine> RecipeLines { get; }
    DbSet<Domain.Stock.Supplier> Suppliers { get; }
    DbSet<Domain.Stock.Delivery> Deliveries { get; }
    DbSet<Domain.Stock.DeliveryLine> DeliveryLines { get; }
    DbSet<Domain.Stock.WasteRecord> WasteRecords { get; }
    DbSet<Domain.Stock.StockTransfer> StockTransfers { get; }
    DbSet<Domain.Stock.StockTransferLine> StockTransferLines { get; }
    DbSet<Domain.Stock.StockCount> StockCounts { get; }
    DbSet<Domain.Stock.StockCountLine> StockCountLines { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
