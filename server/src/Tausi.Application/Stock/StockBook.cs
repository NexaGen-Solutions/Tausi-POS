using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.Stock;

namespace Tausi.Application.Stock;

/// <summary>The two stock operations whose SQL differs per database.</summary>
public interface IStockDatabase
{
    /// <summary>Locks these stock items' rows until the transaction ends (their average cost changes).</summary>
    Task LockItems(IReadOnlyCollection<Guid> itemIds, CancellationToken ct);
}

/// <summary>What moved, for the caller's journal entry: the quantity and its value at the movement's cost.</summary>
public sealed record Moved(StockMovement Movement)
{
    public decimal Value => Movement.Quantity * Movement.UnitCost;
}

/// <summary>
/// Every stock movement goes through here: it adds the movement and, in the same transaction, the
/// same quantity to the cached on hand (one atomic update, so two prints at once can't lose one).
/// The caller must have a transaction open and save its changes.
/// </summary>
public sealed class StockBook(ITausiDb db, IStockDatabase stockDb, TimeProvider clock)
{
    public async Task<Moved> Move(
        Guid itemId, Guid locationId, decimal quantity, decimal unitCost, StockMovementKind kind,
        string referenceType, Guid? referenceId, Guid? userId, Guid? workPeriodId, CancellationToken ct,
        decimal? lineQuantity = null, DateTimeOffset? at = null)
    {
        var movement = new StockMovement
        {
            StockItemId = itemId, LocationId = locationId, Quantity = quantity, UnitCost = unitCost, Kind = kind,
            ReferenceType = referenceType, ReferenceId = referenceId, LineQuantity = lineQuantity, UserId = userId,
            WorkPeriodId = workPeriodId, At = at ?? clock.GetUtcNow(),
        };
        // Training sales never move stock: the movement is made for the caller but not saved.
        if (db.TrainingMode) return new Moved(movement);
        db.StockMovements.Add(movement);
        await AddToOnHand(itemId, locationId, quantity, ct);
        return new Moved(movement);
    }

    private async Task AddToOnHand(Guid itemId, Guid locationId, decimal quantity, CancellationToken ct)
    {
        var tracked = db.StockLevels.Local.FirstOrDefault(l => l.StockItemId == itemId && l.LocationId == locationId);
        if (tracked is not null && db is DbContext context && context.Entry(tracked).State == EntityState.Added)
        {
            tracked.OnHand += quantity;
            return;
        }
        var updated = await db.StockLevels.Where(l => l.StockItemId == itemId && l.LocationId == locationId)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.OnHand, l => l.OnHand + quantity), ct);
        if (tracked is not null) tracked.OnHand += quantity;
        if (updated == 0) db.StockLevels.Add(new StockLevel { StockItemId = itemId, LocationId = locationId, OnHand = quantity });
    }

    /// <summary>Makes sure every item has a level row at every location (so updates never race to insert).</summary>
    public async Task EnsureLevels(CancellationToken ct)
    {
        var items = await db.StockItems.Select(i => i.Id).ToListAsync(ct);
        var locations = await db.StockLocations.Select(l => l.Id).ToListAsync(ct);
        var existing = (await db.StockLevels.Select(l => new { l.StockItemId, l.LocationId }).ToListAsync(ct))
            .Select(l => (l.StockItemId, l.LocationId)).ToHashSet();
        foreach (var item in items)
        foreach (var location in locations)
            if (existing.Add((item, location)))
                db.StockLevels.Add(new StockLevel { StockItemId = item, LocationId = location });
    }

    /// <summary>The item's on hand across all locations (the average cost is business-wide).</summary>
    public Task<decimal> TotalOnHand(Guid itemId, CancellationToken ct) =>
        db.StockLevels.Where(l => l.StockItemId == itemId).SumAsync(l => l.OnHand, ct);

    public Task LockItems(IReadOnlyCollection<Guid> itemIds, CancellationToken ct) => stockDb.LockItems(itemIds, ct);

    /// <summary>
    /// The weighted average after a delivery: (on hand × average + delivered × cost) ÷ (on hand +
    /// delivered); the delivered cost when nothing (or less) was on hand.
    /// </summary>
    public static decimal AverageAfterDelivery(decimal onHand, decimal average, decimal delivered, decimal cost) =>
        onHand <= 0 || onHand + delivered <= 0 ? cost : (onHand * average + delivered * cost) / (onHand + delivered);

    /// <summary>The average after taking a delivery back out at its cost; unchanged if nothing would be left.</summary>
    public static decimal AverageAfterReversal(decimal onHand, decimal average, decimal reversed, decimal cost) =>
        onHand - reversed <= 0 ? average : (onHand * average - reversed * cost) / (onHand - reversed);
}
