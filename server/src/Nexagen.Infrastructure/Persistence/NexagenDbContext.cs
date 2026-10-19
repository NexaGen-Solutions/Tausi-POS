using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Domain.Audit;
using Nexagen.Domain.Common;
using Nexagen.Domain.Devices;
using Nexagen.Domain.Ledger;
using Nexagen.Domain.Money;
using Nexagen.Domain.People;
using Nexagen.Domain.Platform;
using Nexagen.Domain.Printing;
using Nexagen.Domain.Settings;

namespace Nexagen.Infrastructure.Persistence;

/// <summary>
/// The one EF Core model. Each database provider has a small subclass
/// (<see cref="PostgresNexagenDbContext"/>, <see cref="SqlServerNexagenDbContext"/>) that owns its
/// own migrations, so the model stays the same and only column details differ.
/// </summary>
public class NexagenDbContext(DbContextOptions options) : DbContext(options), INexagenDb
{
    public DbSet<BusinessSettings> BusinessSettings => Set<BusinessSettings>();
    public DbSet<Licence> Licences => Set<Licence>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApprovalGrant> ApprovalGrants => Set<ApprovalGrant>();
    public DbSet<PinFailure> PinFailures => Set<PinFailure>();
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

    /// <summary>Filter for the "active users have unique PINs" index; the SQL differs per provider.</summary>
    protected virtual string? ActiveUserFilter => null;

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
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<BusinessSettings>(e =>
        {
            e.Property(x => x.VatRate).HasPrecision(5, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.ReceiptFooter).HasMaxLength(1000);
            e.Property(x => x.OtherJson).Unbounded();
        });

        m.Entity<Licence>(e => e.Property(x => x.Key).HasMaxLength(500));

        m.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
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

        m.Entity<Terminal>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.DeviceTokenHash).HasMaxLength(64);
            e.HasIndex(x => x.DeviceTokenHash).IsUnique();
            e.Property(x => x.AppVersion).HasMaxLength(50);
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
