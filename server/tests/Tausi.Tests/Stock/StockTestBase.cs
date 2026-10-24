using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Stock;
using Tausi.Domain.Ledger;
using Tausi.Domain.Stock;
using Tausi.Tests.Tickets;

namespace Tausi.Tests.Stock;

/// <summary>The demo with its stock (Store and Bar opening counts, recipes, KBL), and helpers to read it.</summary>
public abstract class StockTestBase : ApprovalsFixtureBase
{
    protected Guid Item(string name) => F.Read().StockItems.Single(i => i.Name == name).Id;
    protected Guid Location(string name) => F.Read().StockLocations.Single(l => l.Name == name).Id;
    protected Guid Supplier(string name = "KBL") => F.Read().Suppliers.Single(s => s.Name == name).Id;

    protected decimal OnHand(string item, string location) =>
        F.Read().StockLevels.Single(l => l.StockItemId == Item(item) && l.LocationId == Location(location)).OnHand;

    protected decimal Average(string item) => F.Read().StockItems.Single(i => i.Name == item).AverageCost;

    protected List<StockMovement> Movements(StockMovementKind kind) =>
        F.Read().StockMovements.Where(m => m.Kind == kind).ToList();

    protected List<JournalEntry> Entries(string sourceType) =>
        F.Read().JournalEntries.Include(e => e.Lines).ThenInclude(l => l.Account).Where(e => e.SourceType == sourceType).ToList();

    protected static decimal Dr(JournalEntry e, string code) => e.Lines.Where(l => l.Account!.Code == code).Sum(l => l.Debit);
    protected static decimal Cr(JournalEntry e, string code) => e.Lines.Where(l => l.Account!.Code == code).Sum(l => l.Credit);

    /// <summary>The cached on hand equals the sum of the movements, for every item at every location.</summary>
    protected void CacheMatchesMovements()
    {
        var db = F.Read();
        var sums = db.StockMovements.GroupBy(m => new { m.StockItemId, m.LocationId })
            .Select(g => new { g.Key.StockItemId, g.Key.LocationId, Sum = g.Sum(m => m.Quantity) }).ToList();
        foreach (var level in db.StockLevels.ToList())
            level.OnHand.Should().Be(sums.FirstOrDefault(s => s.StockItemId == level.StockItemId && s.LocationId == level.LocationId)?.Sum ?? 0,
                "the cached on hand is the sum of the movements");
    }

    /// <summary>Every journal entry so far balances.</summary>
    protected void AllEntriesBalance() =>
        F.Read().JournalEntries.Include(e => e.Lines).ToList()
            .Should().OnlyContain(e => e.Lines.Sum(l => l.Debit) == e.Lines.Sum(l => l.Credit) && e.Lines.Count >= 2);

    protected async Task AsAdmin() => await F.LoginOn("Administrator", "Counter B");

    protected Task<DeliveryDto> Deliver(string invoice, DeliveryPayment payment = DeliveryPayment.OnCredit, string location = "Store",
        DateOnly? date = null, string item = "Tusker Lager", decimal quantity = 4, decimal cost = 2500, string? unit = "crate", string? reference = null) =>
        F.Get<DeliveryCommands>().Receive(new ReceiveDeliveryRequest(Supplier(), invoice, date, Location(location), payment, reference,
            [new DeliveryLineRequest(Item(item), unit, quantity, cost)]), None);
}
