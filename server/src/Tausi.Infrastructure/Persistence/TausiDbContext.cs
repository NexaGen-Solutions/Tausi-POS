using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.Audit;
using Tausi.Domain.Common;
using Tausi.Domain.Devices;
using Tausi.Domain.Ledger;
using Tausi.Domain.Menu;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Platform;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;
using Tausi.Domain.Settings;

namespace Tausi.Infrastructure.Persistence;

/// <summary>
/// The one EF Core model. Each database provider has a small subclass
/// (<see cref="PostgresTausiDbContext"/>, <see cref="SqlServerTausiDbContext"/>) that owns its
/// own migrations, so the model stays the same and only column details differ.
/// </summary>
public class TausiDbContext(DbContextOptions options) : DbContext(options), ITausiDb
{
    public DbSet<BusinessSettings> BusinessSettings => Set<BusinessSettings>();
    public DbSet<Licence> Licences => Set<Licence>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApprovalGrant> ApprovalGrants => Set<ApprovalGrant>();
    public DbSet<PinFailure> PinFailures => Set<PinFailure>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<WorkPeriod> WorkPeriods => Set<WorkPeriod>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<TerminalPairing> TerminalPairings => Set<TerminalPairing>();
    public DbSet<Printer> Printers => Set<Printer>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<MenuItemPrice> MenuItemPrices => Set<MenuItemPrice>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<ModifierGroup> ModifierGroups => Set<ModifierGroup>();
    public DbSet<ModifierOption> ModifierOptions => Set<ModifierOption>();
    public DbSet<ModifierGroupAssignment> ModifierGroupAssignments => Set<ModifierGroupAssignment>();
    public DbSet<PriceChangeLog> PriceChangeLogs => Set<PriceChangeLog>();
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<DiningTable> Tables => Set<DiningTable>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<VoidLog> VoidLogs => Set<VoidLog>();
    public DbSet<GiftLog> GiftLogs => Set<GiftLog>();
    public DbSet<DiscountLog> DiscountLogs => Set<DiscountLog>();

    /// <summary>The database sequence for ticket numbers (see <see cref="TicketNumberSequence"/>).</summary>
    public const string TicketNumberSequence = "TicketNumbers";

    /// <summary>Ticket numbers start here, so they look like the four-digit numbers staff know.</summary>
    public const long FirstTicketNumber = 1001;

    /// <summary>True for databases with sequences (PostgreSQL, SQL Server); SQLite in unit tests has none.</summary>
    protected virtual bool HasSequences => false;

    /// <summary>Filter for the "active users have unique PINs" index; the SQL differs per provider.</summary>
    protected virtual string? ActiveUserFilter => null;

    /// <summary>Filter for the "only one open work period" index; the SQL differs per provider.</summary>
    protected virtual string? OpenWorkPeriodFilter => null;

    /// <summary>Filter for the "one default price list" index; the SQL differs per provider.</summary>
    protected virtual string? DefaultPriceListFilter => null;

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<decimal>().HavePrecision(18, 2);
        b.Properties<string>().HaveMaxLength(200);

        // Enums are stored as their names: readable in the database and safe to reorder.
        b.Properties<PermissionValue>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<PrintMode>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<RoundingRule>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<PrinterConnection>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<PrinterStatus>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<PrintJobKind>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<PrintJobStatus>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<AccountType>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<TicketStatus>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<OrderLineStatus>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<DiscountKind>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<ApprovalKind>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<ApprovalStatus>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<PriceChangeKind>().HaveConversion<string>().HaveMaxLength(20);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<BusinessSettings>(e =>
        {
            e.Property(x => x.VatRate).HasPrecision(5, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.TimeZone).HasMaxLength(BusinessTimeZone.MaxLength);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.ReceiptFooter).HasMaxLength(1000);
            e.Property(x => x.OtherJson).Unbounded();
        });

        m.Entity<Licence>(e => e.Property(x => x.Key).HasMaxLength(500));

        m.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.SystemKey).HasMaxLength(20);
            e.HasIndex(x => x.SystemKey).IsUnique();
            e.HasMany(x => x.Permissions).WithOne(x => x.Role).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<RolePermission>(e =>
        {
            e.Property(x => x.PermissionKey).HasMaxLength(64);
            e.HasIndex(x => new { x.RoleId, x.PermissionKey }).IsUnique();
        });

        m.Entity<User>(e =>
        {
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.PinLookup).HasMaxLength(64);
            e.Property(x => x.PinHash).HasMaxLength(200);
            e.Property(x => x.UserName).HasMaxLength(100);
            e.Property(x => x.NormalizedName).HasMaxLength(200);
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.HasIndex(x => x.UserName).IsUnique();
            var pinIndex = e.HasIndex(x => x.PinLookup).IsUnique();
            if (ActiveUserFilter is not null) pinIndex.HasFilter(ActiveUserFilter);
        });

        m.Entity<ApprovalGrant>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.PermissionKey).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        m.Entity<PinFailure>(e => e.HasIndex(x => new { x.TerminalId, x.At }));

        m.Entity<UserSession>(e =>
        {
            e.Property(x => x.EndReason).HasMaxLength(20);
            e.HasIndex(x => new { x.TerminalId, x.EndedAt });
            e.HasIndex(x => x.UserId);
        });

        m.Entity<WorkPeriod>(e =>
        {
            var openIndex = e.HasIndex(x => x.IsOpen).IsUnique();
            if (OpenWorkPeriodFilter is not null) openIndex.HasFilter(OpenWorkPeriodFilter);
            e.HasIndex(x => x.OpenedAt);
        });

        m.Entity<Terminal>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.DeviceTokenHash).HasMaxLength(64);
            e.HasIndex(x => x.DeviceTokenHash).IsUnique();
            e.Property(x => x.AppVersion).HasMaxLength(50);
            e.Property(x => x.DispenseHeader).HasMaxLength(40);
            e.Property(x => x.LocalPrintersJson).Unbounded();
            e.Ignore(x => x.IsPaired);
            e.Ignore(x => x.EffectiveDispenseHeader);
            e.HasOne(x => x.DispensePrinter).WithMany().HasForeignKey(x => x.DispensePrinterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReceiptPrinter).WithMany().HasForeignKey(x => x.ReceiptPrinterId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<TerminalPairing>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(10);
            e.Property(x => x.SecretHash).HasMaxLength(64);
            e.Property(x => x.PendingDeviceToken).HasMaxLength(100);
            e.HasIndex(x => x.Code);
        });

        m.Entity<Printer>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.DispenseTemplate).HasMaxLength(40);
            e.Property(x => x.DispenseHeader).HasMaxLength(40);
            e.HasOne(x => x.OwningTerminal).WithMany().HasForeignKey(x => x.OwningTerminalId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<AuditEntry>(e =>
        {
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.EntityType).HasMaxLength(64);
            e.Property(x => x.EntityId).HasMaxLength(64);
            e.Property(x => x.OldValueJson).Unbounded();
            e.Property(x => x.NewValueJson).Unbounded();
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });

        m.Entity<IdempotencyRecord>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Caller).HasMaxLength(64);
            e.Property(x => x.Endpoint).HasMaxLength(300);
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.Property(x => x.ResponseContentType).HasMaxLength(100);
            e.Property(x => x.ResponseBody).Unbounded();
            e.HasIndex(x => x.Key).IsUnique();
            e.HasIndex(x => x.CreatedAt);
        });

        m.Entity<StoredFile>(e => e.Property(x => x.ContentType).HasMaxLength(100));

        m.Entity<PrintJob>(e =>
        {
            e.HasOne(x => x.Printer).WithMany().HasForeignKey(x => x.PrinterId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.TextPreview).Unbounded();
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.DiscardReason).HasMaxLength(500);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => x.TicketId);
        });

        m.Entity<LedgerAccount>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(32);
            e.HasIndex(x => x.Code).IsUnique();
        });

        m.Entity<JournalEntry>(e =>
        {
            e.Property(x => x.Memo).HasMaxLength(500);
            e.Property(x => x.SourceType).HasMaxLength(64);
            e.Property(x => x.SourceId).HasMaxLength(64);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SourceType, x.SourceId });
            e.HasIndex(x => x.Date);
        });

        m.Entity<JournalLine>(e =>
        {
            e.Property(x => x.Memo).HasMaxLength(500);
            e.Property(x => x.SourceType).HasMaxLength(64);
            e.Property(x => x.SourceId).HasMaxLength(64);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<Category>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60);
            e.HasIndex(x => x.Name).IsUnique();
        });

        m.Entity<MenuItem>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(MenuItem.MaxNameLength);
            e.Property(x => x.ShortName).HasMaxLength(MenuItem.MaxShortNameLength);
            e.Property(x => x.ErpItemCode).HasMaxLength(140);
            e.Property(x => x.VatRate).HasPrecision(5, 2);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => new { x.CategoryId, x.SortOrder });
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Prices).WithOne().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<MenuItemPrice>(e =>
        {
            e.HasIndex(x => new { x.MenuItemId, x.PriceListId }).IsUnique();
            e.HasOne(x => x.PriceList).WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<PriceList>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(40);
            e.HasIndex(x => x.Name).IsUnique();
            var defaultIndex = e.HasIndex(x => x.IsDefault).IsUnique();
            if (DefaultPriceListFilter is not null) defaultIndex.HasFilter(DefaultPriceListFilter);
        });

        m.Entity<ModifierGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasMany(x => x.Options).WithOne().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<ModifierOption>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60);
            e.HasIndex(x => new { x.GroupId, x.Name }).IsUnique();
        });

        m.Entity<ModifierGroupAssignment>(e =>
        {
            e.HasOne<ModifierGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MenuItem>().WithMany().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Restrict);
            // A group is given to a category, or to an item, once.
            e.HasIndex(x => new { x.GroupId, x.CategoryId }).IsUnique();
            e.HasIndex(x => new { x.GroupId, x.MenuItemId }).IsUnique();
        });

        m.Entity<PriceChangeLog>(e =>
        {
            e.Property(x => x.Source).HasMaxLength(20);
            e.HasIndex(x => x.TicketId);
            e.Property(x => x.OldPrice).HasPrecision(18, 2);
            e.Property(x => x.NewPrice).HasPrecision(18, 2);
            e.HasIndex(x => new { x.MenuItemId, x.At });
            e.HasIndex(x => x.At);
        });

        m.Entity<Terminal>(e =>
            e.HasOne<PriceList>().WithMany().HasForeignKey(x => x.PriceListId).OnDelete(DeleteBehavior.Restrict));

        m.Entity<Area>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Area.MaxNameLength);
            e.HasIndex(x => x.Name).IsUnique();
        });

        m.Entity<DiningTable>(e =>
        {
            e.ToTable("Tables");
            e.Property(x => x.Name).HasMaxLength(DiningTable.MaxNameLength);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => new { x.AreaId, x.SortOrder });
            e.HasOne(x => x.Area).WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Restrict);
        });

        if (HasSequences)
            m.HasSequence<long>(TicketNumberSequence).StartsAt(FirstTicketNumber).IncrementsBy(1);

        m.Entity<Ticket>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.Status, x.TableId });
            e.Property(x => x.CustomerName).HasMaxLength(Ticket.MaxCustomerNameLength);
            e.Property(x => x.Note).HasMaxLength(Ticket.MaxNoteLength);
            e.Ignore(x => x.IsOpen);
            e.Ignore(x => x.Total);
            e.Ignore(x => x.Subtotal);
            e.Ignore(x => x.DiscountTotal);
            e.Ignore(x => x.DiscountableLines);
            e.Ignore(x => x.NewLineCount);
            e.Property(x => x.DiscountReason).HasMaxLength(ApprovalRequest.MaxNoteLength + 30);
            e.HasOne(x => x.Table).WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.OpenedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Terminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WorkPeriod>().WithMany().HasForeignKey(x => x.WorkPeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.MergedIntoTicketId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<OrderLine>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(MenuItem.MaxNameLength);
            e.Property(x => x.Options).HasMaxLength(200);
            e.Property(x => x.ShortName).HasMaxLength(MenuItem.MaxShortNameLength);
            e.Property(x => x.OptionIds).HasMaxLength(1000);
            e.Property(x => x.Note).HasMaxLength(OrderLine.MaxNoteLength);
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.Property(x => x.VatRate).HasPrecision(5, 2);
            e.Ignore(x => x.IsVoided);
            e.Ignore(x => x.Counts);
            e.Ignore(x => x.Payable);
            e.Ignore(x => x.IsChangeable);
            e.Property(x => x.VoidReason).HasMaxLength(VoidLog.MaxReasonLength + 30);
            e.HasOne<Printer>().WithMany().HasForeignKey(x => x.DispensePrinterId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.TicketId, x.Status });
            e.HasIndex(x => x.TicketId);
            // Who added a line and where are required (AGENTS.md rule 3).
            e.HasOne<User>().WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict).IsRequired();
            e.HasOne<Terminal>().WithMany().HasForeignKey(x => x.AddedTerminalId).OnDelete(DeleteBehavior.Restrict).IsRequired();
            e.HasOne<MenuItem>().WithMany().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<ApprovalRequest>(e =>
        {
            e.Property(x => x.PayloadJson).Unbounded();
            e.Property(x => x.Summary).HasMaxLength(300);
            e.Property(x => x.Reason).HasMaxLength(ApprovalRequest.MaxNoteLength + 30);
            e.Property(x => x.DecisionNote).HasMaxLength(ApprovalRequest.MaxNoteLength);
            e.Ignore(x => x.IsPending);
            e.HasIndex(x => new { x.Status, x.RequestedAt });
            e.HasIndex(x => new { x.TicketId, x.Status });
            e.HasIndex(x => x.WorkPeriodId);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Terminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<VoidLog>(e =>
        {
            e.Property(x => x.ItemName).HasMaxLength(MenuItem.MaxNameLength);
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.Property(x => x.Reason).HasMaxLength(VoidLog.MaxReasonLength);
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => x.At);
        });

        m.Entity<GiftLog>(e =>
        {
            e.Property(x => x.ItemName).HasMaxLength(MenuItem.MaxNameLength);
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.Property(x => x.Reason).HasMaxLength(VoidLog.MaxReasonLength);
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => x.At);
        });

        m.Entity<DiscountLog>(e =>
        {
            e.Property(x => x.Value).HasPrecision(18, 2);
            e.Property(x => x.OldValue).HasPrecision(18, 2);
            e.Property(x => x.Reason).HasMaxLength(VoidLog.MaxReasonLength);
            e.HasIndex(x => x.TicketId);
        });

        // Every editable entity carries an optimistic concurrency token.
        foreach (var type in m.Model.GetEntityTypes().Where(t => typeof(VersionedEntity).IsAssignableFrom(t.ClrType)))
            m.Entity(type.ClrType).Property(nameof(VersionedEntity.RowVersion)).IsConcurrencyToken();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpRowVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpRowVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// The UPDATE checks the version that was loaded and writes the next one, so a second editor
    /// holding the old version gets a concurrency error instead of overwriting.
    /// </summary>
    private void BumpRowVersions()
    {
        // Names are unique ignoring case (a unique index on NormalizedName); keep it in step.
        foreach (var entry in ChangeTracker.Entries<User>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.NormalizedName = User.Normalize(entry.Entity.Name);
        }

        foreach (var entry in ChangeTracker.Entries<VersionedEntity>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.RowVersion = entry.Property(e => e.RowVersion).OriginalValue + 1;
        }
    }
}

internal static class PropertyBuilderExtensions
{
    /// <summary>Removes the default 200-character limit (text / nvarchar(max)).</summary>
    public static Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> Unbounded<T>(
        this Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> builder)
    {
        builder.Metadata.SetMaxLength(null);
        return builder;
    }
}
