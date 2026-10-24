using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Tausi.Domain.Devices;
using Tausi.Domain.Menu;
using Tausi.Domain.People;
using Tausi.Domain.Stock;

namespace Tausi.Infrastructure.Persistence;

public partial class TausiDbContext
{
    /// <summary>Quantities in base units: 4 decimals covers ml fractions of a bottle.</summary>
    private const int QuantityScale = 4;

    /// <summary>Costs per base unit: 550 ÷ 350 ml needs more than cents.</summary>
    private const int UnitCostScale = 6;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    /// <summary>Filter for the invoice index: a rejected delivery doesn't keep its invoice number. SQL per provider.</summary>
    protected virtual string? NotRejectedFilter => null;

    private void ConfigureStock(ModelBuilder m)
    {
        m.Entity<StockLocation>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(StockLocation.MaxNameLength);
            e.HasIndex(x => x.Name).IsUnique();
        });

        m.Entity<Terminal>(e =>
            e.HasOne<StockLocation>().WithMany().HasForeignKey(x => x.SellsFromLocationId).OnDelete(DeleteBehavior.Restrict));

        m.Entity<StockItem>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(StockItem.MaxNameLength);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.BaseUnit).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.AverageCost).HasPrecision(18, UnitCostScale);
            e.Property(x => x.Packs)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<List<StockPack>>(v, JsonOptions) ?? new List<StockPack>(),
                    new ValueComparer<List<StockPack>>(
                        (a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (h, p) => HashCode.Combine(h, p)), v => v.ToList()))
                .HasMaxLength(1000);
            e.Ignore(x => x.BaseUnitName);
            e.Ignore(x => x.UnitNames);
            e.Ignore(x => x.LargestUnit);
        });

        m.Entity<StockLevel>(e =>
        {
            e.Property(x => x.OnHand).HasPrecision(18, QuantityScale);
            e.Property(x => x.ReorderLevel).HasPrecision(18, QuantityScale);
            e.HasIndex(x => new { x.StockItemId, x.LocationId }).IsUnique();
            e.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StockLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<StockMovement>(e =>
        {
            e.Property(x => x.Quantity).HasPrecision(18, QuantityScale);
            e.Property(x => x.LineQuantity).HasPrecision(18, 3);
            e.Property(x => x.UnitCost).HasPrecision(18, UnitCostScale);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ReferenceType).HasMaxLength(30);
            e.Ignore(x => x.Value);
            e.HasIndex(x => new { x.StockItemId, x.LocationId, x.At });
            e.HasIndex(x => new { x.ReferenceType, x.ReferenceId });
            e.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StockLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<RecipeLine>(e =>
        {
            e.Property(x => x.Quantity).HasPrecision(18, QuantityScale);
            e.Property(x => x.BaseQuantity).HasPrecision(18, QuantityScale);
            e.Property(x => x.Unit).HasMaxLength(StockItem.MaxPackNameLength);
            e.HasIndex(x => x.MenuItemId);
            e.HasOne<MenuItem>().WithMany().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<Supplier>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Supplier.MaxNameLength);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.KraPin).HasMaxLength(20);
            e.Property(x => x.Notes).HasMaxLength(500);
        });

        m.Entity<Delivery>(e =>
        {
            e.Property(x => x.InvoiceNumber).HasMaxLength(Delivery.MaxInvoiceLength);
            e.Property(x => x.PaymentReference).HasMaxLength(Delivery.MaxReferenceLength);
            e.Property(x => x.Payment).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ReversalStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RejectNote).HasMaxLength(Delivery.MaxNoteLength);
            e.Property(x => x.ReversalReason).HasMaxLength(Delivery.MaxNoteLength);
            e.Ignore(x => x.IsStanding);
            // An invoice is recorded once per supplier, even if two people save it at once.
            var invoice = e.HasIndex(x => new { x.SupplierId, x.InvoiceNumber }).IsUnique();
            if (NotRejectedFilter is { } notRejected) invoice.HasFilter(notRejected);
            e.HasIndex(x => x.Date);
            e.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StockLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<DeliveryLine>(e =>
        {
            e.Property(x => x.Unit).HasMaxLength(StockItem.MaxPackNameLength);
            e.Property(x => x.Factor).HasPrecision(18, QuantityScale);
            e.Property(x => x.Quantity).HasPrecision(18, QuantityScale);
            e.Property(x => x.UnitCost).HasPrecision(18, 2);
            e.Ignore(x => x.BaseQuantity);
            e.Ignore(x => x.BaseUnitCost);
            e.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<WasteRecord>(e =>
        {
            e.Property(x => x.Quantity).HasPrecision(18, QuantityScale);
            e.Property(x => x.BaseQuantity).HasPrecision(18, QuantityScale);
            e.Property(x => x.Unit).HasMaxLength(StockItem.MaxPackNameLength);
            e.Property(x => x.Reason).HasMaxLength(30);
            e.Property(x => x.Note).HasMaxLength(WasteRecord.MaxNoteLength);
            e.Property(x => x.RejectNote).HasMaxLength(Delivery.MaxNoteLength);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.CreatedAt);
            e.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StockLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<StockTransfer>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RejectNote).HasMaxLength(Delivery.MaxNoteLength);
            e.HasIndex(x => x.CreatedAt);
            e.HasOne<StockLocation>().WithMany().HasForeignKey(x => x.FromLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StockLocation>().WithMany().HasForeignKey(x => x.ToLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<StockTransferLine>(e =>
        {
            e.Property(x => x.Quantity).HasPrecision(18, QuantityScale);
            e.Property(x => x.BaseQuantity).HasPrecision(18, QuantityScale);
            e.Property(x => x.Unit).HasMaxLength(StockItem.MaxPackNameLength);
            e.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<StockCount>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RejectNote).HasMaxLength(StockCount.MaxNoteLength);
            e.HasIndex(x => new { x.LocationId, x.Status });
            e.HasOne<StockLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CountedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.CountId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<StockCountLine>(e =>
        {
            e.Property(x => x.Counted).HasPrecision(18, QuantityScale);
            e.Property(x => x.Expected).HasPrecision(18, QuantityScale);
            e.Property(x => x.UnitCost).HasPrecision(18, UnitCostScale);
            e.Property(x => x.Entered)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<Dictionary<string, decimal>>(v, JsonOptions) ?? new Dictionary<string, decimal>(),
                    new ValueComparer<Dictionary<string, decimal>>(
                        (a, b) => a!.Count == b!.Count && !a.Except(b).Any(), v => v.Count, v => new Dictionary<string, decimal>(v)))
                .HasMaxLength(500);
            e.Ignore(x => x.Difference);
            e.HasIndex(x => new { x.CountId, x.StockItemId }).IsUnique();
            e.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
