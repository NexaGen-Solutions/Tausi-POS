using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Approvals;
using Tausi.Application.Settings;
using Tausi.Application.Stock;
using Tausi.Application.Tickets;
using Tausi.Domain.Ledger;
using Tausi.Domain.Service;
using Tausi.Domain.Stock;

namespace Tausi.Tests.Stock;

public class SellingStockTests : StockTestBase
{
    [Fact]
    public async Task Printing_takes_the_recipe_out_of_the_printing_terminals_location_with_one_cost_of_sales_entry_per_round()
    {
        var tusker = OnHand("Tusker Lager", "Bar");
        var viceroy = OnHand("Viceroy 375ml", "Bar");
        var store = OnHand("Tusker Lager", "Store");

        var ticket = await Printed("7", ("Tusker Lager", 2), ("Viceroy Tot", 1));

        OnHand("Tusker Lager", "Bar").Should().Be(tusker - 2);
        OnHand("Viceroy 375ml", "Bar").Should().Be(viceroy - 25, "a tot is 25 ml of the 375 ml bottle");
        OnHand("Tusker Lager", "Store").Should().Be(store, "Counter A sells from the Bar");
        var sales = Movements(StockMovementKind.Sale);
        sales.Should().HaveCount(2).And.OnlyContain(m => m.LocationId == Location("Bar"));
        var entry = Entries(StockSelling.SaleSource).Single();
        entry.SourceId.Should().Be($"{ticket.Id}:1");
        // 2 × 100 (a crate of 25 at 2,500) + 25 ml × 1.60 (600 a bottle of 375 ml).
        Dr(entry, LedgerAccountCodes.CostOfSales).Should().Be(240m);
        Cr(entry, LedgerAccountCodes.Stock).Should().Be(240m);

        await F.Get<AddLine>().Execute(ticket.Id, new AddLineRequest(F.Item("Guiness"), F.DefaultOptions("Guiness"), 1), None);
        Later();
        await F.Get<PrintTicket>().Print(ticket.Id, None);
        Entries(StockSelling.SaleSource).Should().HaveCount(2, "one entry per print round");
        Movements(StockMovementKind.Sale).Should().HaveCount(3, "only the new line moves stock in round 2");
        CacheMatchesMovements();
        AllEntriesBalance();
    }

    [Fact]
    public async Task A_void_not_poured_goes_back_to_stock_and_reverses_its_cost_of_sales()
    {
        var before = OnHand("Tusker Lager", "Bar");
        var ticket = await Printed("7", ("Tusker Lager", 2));
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);

        await Void(ticket.Id, LineOf(ticket, "Tusker Lager"), 1, poured: false);

        OnHand("Tusker Lager", "Bar").Should().Be(before - 1);
        Movements(StockMovementKind.VoidReturn).Single().Should().Match<StockMovement>(m => m.Quantity == 1 && m.UnitCost == 100m);
        var entry = Entries(StockSelling.VoidSource).Single();
        Dr(entry, LedgerAccountCodes.Stock).Should().Be(100m);
        Cr(entry, LedgerAccountCodes.CostOfSales).Should().Be(100m);
        CacheMatchesMovements();
    }

    [Fact]
    public async Task A_poured_void_stays_out_and_its_cost_moves_to_stock_loss()
    {
        var before = OnHand("Viceroy 375ml", "Bar");
        var ticket = await Printed("7", ("Viceroy Tot", 1));
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);

        await Void(ticket.Id, LineOf(ticket, "Viceroy Tot"), 1, poured: true);

        OnHand("Viceroy 375ml", "Bar").Should().Be(before - 25);
        Movements(StockMovementKind.VoidReturn).Should().BeEmpty();
        var entry = Entries(StockSelling.VoidSource).Single();
        Dr(entry, LedgerAccountCodes.StockLoss).Should().Be(40m);
        Cr(entry, LedgerAccountCodes.CostOfSales).Should().Be(40m);
    }

    [Fact]
    public async Task With_stock_return_off_every_void_is_poured()
    {
        await F.SetSetting(SettingsRegistry.StockReturnOnVoidIfNotServed, false);
        var ticket = await Printed("7", ("Tusker Lager", 1));
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);

        await Void(ticket.Id, LineOf(ticket, "Tusker Lager"), 1, poured: false);

        Movements(StockMovementKind.VoidReturn).Should().BeEmpty();
        Dr(Entries(StockSelling.VoidSource).Single(), LedgerAccountCodes.StockLoss).Should().Be(100m);
    }

    [Fact]
    public async Task The_approvers_poured_answer_is_what_is_stored()
    {
        var sam = await F.LoginOn("SAM", "Counter B");
        await F.LoginOn("edna", "Pillar 1");
        var ticket = await Printed("7", ("Tusker Lager", 2));
        var request = await Request(ApprovalKind.Void, ticket.Id, LineOf(ticket, "Tusker Lager"),
            new VoidLineRequest(1, "Wrong item", WasPoured: true), sam.UserId);
        F.ActAs(sam);

        await F.Get<ApproveRequest>().Execute(request.Id, None, new ApproveRequestDto(WasPoured: false));

        F.Read().VoidLogs.Single().WasPoured.Should().BeFalse();
        Movements(StockMovementKind.VoidReturn).Should().ContainSingle(m => m.Quantity == 1);
    }

    [Fact]
    public async Task A_gift_stays_in_cost_of_sales()
    {
        var ticket = await Printed("7", ("Tusker Lager", 2));
        var before = OnHand("Tusker Lager", "Bar");
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);

        await Gift(ticket.Id, LineOf(ticket, "Tusker Lager"));

        OnHand("Tusker Lager", "Bar").Should().Be(before);
        F.Read().StockMovements.Count(m => m.Kind != StockMovementKind.Opening).Should().Be(1, "only the sale");
        Entries(StockSelling.VoidSource).Should().BeEmpty();
    }

    [Fact]
    public async Task A_menu_item_without_a_recipe_moves_no_stock_and_is_listed()
    {
        await Printed("7", ("Pool Table", 1));

        Movements(StockMovementKind.Sale).Should().BeEmpty();
        Entries(StockSelling.SaleSource).Should().BeEmpty();
        await AsAdmin();
        (await F.Get<StockCatalog>().Overview(null, None)).NoRecipe.Select(n => n.Name).Should().Contain("Pool Table").And.NotContain("Tusker Lager");
    }

    [Fact]
    public async Task A_partial_void_after_a_split_returns_its_share()
    {
        var ticket = await Printed("7", ("Tusker Lager", 3));
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);
        await Gift(ticket.Id, LineOf(ticket, "Tusker Lager"));
        var detail = await Detail(ticket.Id);
        Later();

        await Void(ticket.Id, detail.Lines.First(l => l.Name == "Tusker Lager" && !l.IsGift && l.Status == "Printed").Id, 2, poured: false);

        Movements(StockMovementKind.VoidReturn).Sum(m => m.Quantity).Should().Be(2);
        CacheMatchesMovements();
    }
}
