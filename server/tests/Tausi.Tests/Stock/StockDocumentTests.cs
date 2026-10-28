using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Application.Stock;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Domain.People;
using Tausi.Domain.Stock;

namespace Tausi.Tests.Stock;

public class StockDocumentTests : StockTestBase
{
    // ---- On hand ----

    [Fact]
    public async Task The_cached_on_hand_always_equals_the_sum_of_movements_over_random_sequences()
    {
        var random = new Random(4471);
        var items = F.Read().StockItems.Select(i => i.Id).ToList();
        var locations = F.Read().StockLocations.Select(l => l.Id).ToList();
        var kinds = Enum.GetValues<StockMovementKind>();
        for (var round = 0; round < 25; round++)
        {
            await using var tx = await F.Db.Database.BeginTransactionAsync();
            var book = F.Get<StockBook>();
            for (var i = 0; i < random.Next(1, 8); i++)
                await book.Move(items[random.Next(items.Count)], locations[random.Next(locations.Count)],
                    decimal.Round((decimal)(random.NextDouble() * 200 - 120), 2), 1m, kinds[random.Next(kinds.Length)], "Test", null, null, null, None);
            await F.Db.SaveChangesAsync();
            await tx.CommitAsync();
            CacheMatchesMovements();
        }
    }

    [Fact]
    public async Task Negative_stock_is_allowed_and_flagged()
    {
        await using (var tx = await F.Db.Database.BeginTransactionAsync())
        {
            await F.Get<StockBook>().Move(Item("Guiness"), Location("Bar"), -500, 120m, StockMovementKind.Sale, "Test", null, null, null, None);
            await F.Db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        await AsAdmin();

        var row = (await F.Get<StockCatalog>().Overview(Location("Bar"), None)).Items.Single(i => i.Name == "Guiness");

        row.OnHand.Should().Be(-475);
        row.Negative.Should().BeTrue();
        row.BelowReorder.Should().BeTrue();
        row.OnHandText.Should().Be("-19 crates");
    }

    [Theory]
    [InlineData(100, 80, 50, 110, 90)] // (100 × 80 + 50 × 110) ÷ 150
    [InlineData(0, 80, 50, 110, 110)]
    [InlineData(-10, 80, 50, 110, 110)]
    public void The_average_cost_after_a_delivery(decimal onHand, decimal average, decimal delivered, decimal cost, decimal expected) =>
        StockBook.AverageAfterDelivery(onHand, average, delivered, cost).Should().Be(expected);

    [Fact]
    public void Quantities_read_in_their_clearest_pack()
    {
        var tusker = new StockItem { Name = "Tusker", BaseUnit = StockUnit.Bottle, Packs = [new("crate", 25)] };
        tusker.Describe(82).Should().Be("3 crates 7 bottles");
        tusker.Describe(1).Should().Be("1 bottle");
        var viceroy = new StockItem { Name = "Viceroy", BaseUnit = StockUnit.Ml, Packs = [new("bottle", 375), new("carton", 9000)] };
        viceroy.Describe(900).Should().Be("2.4 bottles (900 ml)");
        viceroy.FactorOf("carton").Should().Be(9000);
        FluentActions.Invoking(() => viceroy.FactorOf("crate")).Should().Throw<DomainException>();
    }

    // ---- Deliveries ----

    [Fact]
    public async Task A_delivery_in_crates_adds_bottles_at_the_bottle_cost_updates_the_average_and_is_owed()
    {
        await AsAdmin();
        var store = OnHand("Tusker Lager", "Store");
        var total = F.Read().StockLevels.Where(l => l.StockItemId == Item("Tusker Lager")).Sum(l => l.OnHand);

        var delivery = await Deliver("4471", cost: 2750);

        OnHand("Tusker Lager", "Store").Should().Be(store + 100);
        var movement = Movements(StockMovementKind.Delivery).Single();
        movement.Quantity.Should().Be(100);
        movement.UnitCost.Should().Be(110m);
        Average("Tusker Lager").Should().Be(decimal.Round((total * 100m + 100 * 110m) / (total + 100), 6));
        delivery.Total.Should().Be(11000m);
        var entry = Entries(DeliveryCommands.LedgerSource).Single();
        Dr(entry, LedgerAccountCodes.Stock).Should().Be(11000m);
        Cr(entry, LedgerAccountCodes.SupplierPayables).Should().Be(11000m);
        (await F.Get<SupplierCommands>().List(None)).Single(s => s.Name == "KBL").Owed.Should().Be(11000m);
    }

    [Theory]
    [InlineData(DeliveryPayment.Cash, LedgerAccountCodes.Safe, null)]
    [InlineData(DeliveryPayment.Mpesa, LedgerAccountCodes.MPesa, "QWE123")]
    [InlineData(DeliveryPayment.Bank, LedgerAccountCodes.Bank, "EFT 77")]
    public async Task Each_payment_type_credits_its_account(DeliveryPayment payment, string account, string? reference)
    {
        await AsAdmin();
        await Deliver("100", payment, reference: reference);
        Cr(Entries(DeliveryCommands.LedgerSource).Single(), account).Should().Be(10000m);
        (await F.Get<SupplierCommands>().List(None)).Single(s => s.Name == "KBL").Owed.Should().Be(0m);
    }

    [Fact]
    public async Task An_invoice_is_recorded_once_per_supplier_and_never_in_the_future()
    {
        await AsAdmin();
        await Deliver("4471", date: new DateOnly(2026, 10, 12));

        var again = () => Deliver("4471");
        (await again.Should().ThrowAsync<ConflictException>()).WithMessage("Invoice 4471 from KBL is already recorded on 12 Oct.");
        F.Db.Suppliers.Add(new Supplier { Name = "Coca-Cola" });
        await F.Db.SaveChangesAsync();
        await F.Get<DeliveryCommands>().Receive(new ReceiveDeliveryRequest(Supplier("Coca-Cola"), "4471", null, Location("Store"),
            DeliveryPayment.OnCredit, null, [new DeliveryLineRequest(Item("Sodas 300ml"), "crate", 1, 1200)]), None);

        var future = () => Deliver("9", date: DateOnly.FromDateTime(F.Clock.GetUtcNow().Date).AddDays(2));
        await future.Should().ThrowAsync<DomainException>().WithMessage("The delivery date can't be in the future.");
    }

    [Fact]
    public async Task The_last_cost_paid_this_supplier_for_the_pack_is_suggested()
    {
        await AsAdmin();
        await Deliver("1", cost: 2600);
        F.Clock.Advance(TimeSpan.FromMinutes(1));
        await Deliver("2", cost: 2650);

        (await F.Get<DeliveryCommands>().LastCost(Supplier(), Item("Tusker Lager"), "crate", None)).UnitCost.Should().Be(2650m);
        (await F.Get<DeliveryCommands>().LastCost(Supplier(), Item("Guiness"), "crate", None)).UnitCost.Should().BeNull();
    }

    [Fact]
    public async Task A_reversal_takes_it_back_out_with_a_reversing_entry_and_is_refused_once_some_was_sold()
    {
        await AsAdmin();
        var first = await Deliver("1", location: "Bar", item: "Heineken", quantity: 1, cost: 3600);
        var before = OnHand("Heineken", "Bar");

        var reversed = await F.Get<DeliveryCommands>().Reverse(first.Id, new ReverseDeliveryRequest("Wrong invoice"), None);

        reversed.ReversalStatus.Should().Be("Posted");
        OnHand("Heineken", "Bar").Should().Be(before - 24);
        var entry = Entries(DeliveryCommands.LedgerSource).Single(e => e.SourceId!.EndsWith(":reversal"));
        Dr(entry, LedgerAccountCodes.SupplierPayables).Should().Be(3600m);
        Cr(entry, LedgerAccountCodes.Stock).Should().Be(3600m);
        (await F.Get<SupplierCommands>().List(None)).Single(s => s.Name == "KBL").Owed.Should().Be(0m);

        // 4 crates into the Bar, then more than what was on hand before is sold: some of it is gone.
        var second = await Deliver("2", location: "Bar", item: "Heineken", quantity: 1, cost: 3600);
        await using (var tx = await F.Db.Database.BeginTransactionAsync())
        {
            await F.Get<StockBook>().Move(Item("Heineken"), Location("Bar"), -(OnHand("Heineken", "Bar") - 10), 150m, StockMovementKind.Sale, "Test", null, null, null, None);
            await F.Db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        var refused = () => F.Get<DeliveryCommands>().Reverse(second.Id, new ReverseDeliveryRequest("Wrong"), None);
        (await refused.Should().ThrowAsync<ConflictException>()).WithMessage("Some of this delivery has already been sold. Count the stock instead.");
        AllEntriesBalance();
    }

    // ---- Waste and transfers ----

    [Fact]
    public async Task Waste_goes_to_stock_loss_and_a_transfer_leaves_the_total_unchanged()
    {
        await AsAdmin();
        var bar = OnHand("Guiness", "Bar");

        var waste = await F.Get<WasteAndTransferCommands>().RecordWaste(
            new RecordWasteRequest(Location("Bar"), Item("Guiness"), 1, "bottle", "Broken", null), None);

        waste.Status.Should().Be("Posted");
        OnHand("Guiness", "Bar").Should().Be(bar - 1);
        var entry = Entries(WasteAndTransferCommands.WasteSource).Single();
        Dr(entry, LedgerAccountCodes.StockLoss).Should().Be(120m);
        Cr(entry, LedgerAccountCodes.Stock).Should().Be(120m);
        await FluentActions.Awaiting(() => F.Get<WasteAndTransferCommands>().RecordWaste(
            new RecordWasteRequest(Location("Bar"), Item("Guiness"), 1, null, "Other", null), None)).Should().ThrowAsync<DomainException>();

        var store = OnHand("Tusker Lager", "Store");
        var barTusker = OnHand("Tusker Lager", "Bar");
        var entries = F.Read().JournalEntries.Count();
        await F.Get<WasteAndTransferCommands>().RecordTransfer(new TransferRequest(Location("Store"), Location("Bar"),
            [new TransferLineRequest(Item("Tusker Lager"), 2, "crate")], null), None);

        OnHand("Tusker Lager", "Store").Should().Be(store - 50);
        OnHand("Tusker Lager", "Bar").Should().Be(barTusker + 50);
        F.Read().JournalEntries.Count().Should().Be(entries, "the average cost is business-wide: no entry");
        CacheMatchesMovements();
    }

    // ---- Needs approval on the admin site ----

    [Fact]
    public async Task With_needs_approval_a_document_waits_and_posts_only_when_someone_else_allowed_approves()
    {
        await F.SetPermission("Supervisor", PermissionKeys.StockReceive, PermissionValue.NeedsApproval);
        var sam = await F.LoginOn("SAM", "Counter A");
        var store = OnHand("Tusker Lager", "Store");

        var delivery = await Deliver("55");

        delivery.Status.Should().Be("Pending");
        OnHand("Tusker Lager", "Store").Should().Be(store);
        Entries(DeliveryCommands.LedgerSource).Should().BeEmpty();
        (await F.Get<AwaitingApproval>().Execute(None)).Should().ContainSingle(a => a.Id == delivery.Id && a.Kind == "Delivery");
        await FluentActions.Awaiting(() => F.Get<DeliveryCommands>().Approve(delivery.Id, None)).Should().ThrowAsync<PermissionDeniedException>();

        await AsAdmin();
        var approved = await F.Get<DeliveryCommands>().Approve(delivery.Id, None);
        approved.Status.Should().Be("Posted");
        approved.ApprovedBy.Should().Be("Administrator");
        OnHand("Tusker Lager", "Store").Should().Be(store + 100);

        F.ActAs(sam);
        var waste = await F.Get<WasteAndTransferCommands>().RecordWaste(
            new RecordWasteRequest(Location("Bar"), Item("Guiness"), 1, null, "Expired", null), None);
        waste.Status.Should().Be("Posted", "SAM is allowed stock.waste");
        await F.SetPermission("Supervisor", PermissionKeys.StockWaste, PermissionValue.NeedsApproval);
        var pending = await F.Get<WasteAndTransferCommands>().RecordWaste(
            new RecordWasteRequest(Location("Bar"), Item("Guiness"), 1, null, "Expired", null), None);
        var losses = Entries(WasteAndTransferCommands.WasteSource).Count;
        await AsAdmin();
        (await F.Get<WasteAndTransferCommands>().Reject(pending.Id, new RejectStockRequest("Not broken"), None)).Status.Should().Be("Rejected");
        Entries(WasteAndTransferCommands.WasteSource).Should().HaveCount(losses, "rejecting posts nothing");
    }

    [Fact]
    public async Task Nobody_approves_their_own_stock_document()
    {
        await F.SetPermission("Admin", PermissionKeys.StockReceive, PermissionValue.NeedsApproval);
        await F.SetPermission("Supervisor", PermissionKeys.StockReceive, PermissionValue.Allowed);
        await AsAdmin();
        var delivery = await Deliver("77");
        delivery.Status.Should().Be("Pending");
        await F.SetPermission("Admin", PermissionKeys.StockReceive, PermissionValue.Allowed);

        var own = () => F.Get<DeliveryCommands>().Approve(delivery.Id, None);
        (await own.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be(StockCodes.OwnDocument);
        await F.LoginOn("SAM", "Counter A");
        (await F.Get<DeliveryCommands>().Approve(delivery.Id, None)).Status.Should().Be("Posted");
    }
}
