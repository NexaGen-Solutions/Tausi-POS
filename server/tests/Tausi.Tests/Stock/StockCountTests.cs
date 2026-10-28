using System.Text;
using FluentAssertions;
using Tausi.Application.Common;
using Tausi.Application.Stock;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Domain.Stock;

namespace Tausi.Tests.Stock;

public class StockCountTests : StockTestBase
{
    private StockCountCommands Counts => F.Get<StockCountCommands>();

    /// <summary>SAM starts a Full count of the Bar and counts every item as expected, except the given ones.</summary>
    private async Task<CountDto> CountBar(params (string Item, Dictionary<string, decimal> Entered)[] differences)
    {
        await F.LoginOn("SAM", "Counter B");
        var count = await Counts.Start(new StartCountRequest(Location("Bar"), StockCountKind.Full, false, null), None);
        var entries = count.Lines.Select(l =>
        {
            var different = differences.FirstOrDefault(d => d.Item == l.StockItem);
            return new CountEntry(l.StockItemId, different.Entered ?? new() { [l.BaseUnit] = OnHand(l.StockItem, "Bar") }, null);
        }).ToList();
        return await Counts.Enter(count.Id, new EnterCountsRequest(entries), None);
    }

    [Fact]
    public async Task A_count_is_blind_until_submitted_and_a_full_count_needs_every_item()
    {
        await F.LoginOn("SAM", "Counter B");
        var count = await Counts.Start(new StartCountRequest(Location("Bar"), StockCountKind.Full, false, null), None);
        count = await Counts.Enter(count.Id, new EnterCountsRequest([new CountEntry(Item("Tusker Lager"), new() { ["crate"] = 1, ["bottle"] = 7 }, null)]), None);

        count.Blind.Should().BeTrue();
        count.Lines.Should().OnlyContain(l => l.Expected == null && l.Difference == null && l.Value == null);
        count.Lines.Single(l => l.StockItem == "Tusker Lager").Counted.Should().Be(32);
        count.Uncounted.Should().Be(count.Lines.Count - 1);
        var submit = () => Counts.Submit(count.Id, None);
        (await submit.Should().ThrowAsync<DomainException>()).WithMessage($"{count.Lines.Count - 1} items not counted. Enter 0 if there are none.");
    }

    [Fact]
    public async Task Short_and_over_go_to_stock_loss_and_the_counter_cannot_approve_their_own_count()
    {
        var tusker = OnHand("Tusker Lager", "Bar");
        var count = await CountBar(
            ("Tusker Lager", new() { ["bottle"] = tusker - 3 }),
            ("Guiness", new() { ["bottle"] = OnHand("Guiness", "Bar") + 1 }));
        var submitted = await Counts.Submit(count.Id, None);

        submitted.Blind.Should().BeFalse();
        var line = submitted.Lines.Single(l => l.StockItem == "Tusker Lager");
        line.Difference.Should().Be(-3);
        line.Value.Should().Be(-300m);
        submitted.ItemsShort.Should().Be(1);
        submitted.VarianceValue.Should().Be(-180m, "-300 for Tusker, +120 for Guiness");

        var own = () => Counts.Approve(count.Id, None);
        await own.Should().ThrowAsync<Tausi.Application.Security.PermissionDeniedException>("a supervisor's stock.adjust needs approval");
        await F.SetPermission("Supervisor", Tausi.Domain.People.PermissionKeys.StockAdjust, Tausi.Domain.People.PermissionValue.Allowed);
        (await own.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be(StockCodes.OwnDocument, "even when allowed, not their own count");

        await AsAdmin();
        var approved = await Counts.Approve(count.Id, None);
        approved.Status.Should().Be("Approved");
        OnHand("Tusker Lager", "Bar").Should().Be(tusker - 3);
        var entry = Entries(StockCountCommands.LedgerSource).Single(e => e.SourceId == count.Id.ToString());
        Dr(entry, LedgerAccountCodes.StockLoss).Should().Be(300m);
        Cr(entry, LedgerAccountCodes.Stock).Should().Be(300m);
        Dr(entry, LedgerAccountCodes.Stock).Should().Be(120m);
        Cr(entry, LedgerAccountCodes.StockLoss).Should().Be(120m);
        CacheMatchesMovements();
        AllEntriesBalance();
    }

    [Fact]
    public async Task The_adjustment_uses_the_expected_figure_as_at_the_start_so_sales_during_the_count_are_kept()
    {
        var before = OnHand("Tusker Lager", "Bar");
        var count = await CountBar(); // everything as expected at the start
        F.Clock.Advance(TimeSpan.FromMinutes(5));
        await F.LoginOn("edna", "Counter A");
        await Printed("7", ("Tusker Lager", 2)); // sold while the count is going on
        await F.LoginOn("SAM", "Counter B");
        await Counts.Submit(count.Id, None);

        await AsAdmin();
        await Counts.Approve(count.Id, None);

        OnHand("Tusker Lager", "Bar").Should().Be(before - 2, "the counted figure was right at the start; the 2 sold since stay sold");
        Movements(StockMovementKind.CountAdjust).Should().BeEmpty();
    }

    [Fact]
    public async Task Reject_returns_the_count_to_draft_with_a_note()
    {
        var count = await CountBar(("Tusker Lager", new() { ["bottle"] = 0 }));
        await Counts.Submit(count.Id, None);
        await AsAdmin();

        var rejected = await Counts.Reject(count.Id, new RejectStockRequest("Count the cooler too"), None);

        rejected.Status.Should().Be("Draft");
        rejected.RejectNote.Should().Be("Count the cooler too");
        rejected.Lines.Should().OnlyContain(l => l.Expected == null);
        Movements(StockMovementKind.CountAdjust).Should().BeEmpty();
    }

    [Fact]
    public async Task An_opening_count_sets_the_cost_and_posts_to_opening_balances()
    {
        await F.LoginOn("Administrator", "Counter B");
        F.Db.StockLocations.Add(new StockLocation { Name = "Kitchen", SortOrder = 30 });
        await F.Db.SaveChangesAsync();
        await F.LoginOn("SAM", "Counter B");
        var count = await Counts.Start(new StartCountRequest(Location("Kitchen"), StockCountKind.Partial, true, [Item("Delmonte")]), None);
        await Counts.Enter(count.Id, new EnterCountsRequest([new CountEntry(Item("Delmonte"), new() { ["pack"] = 2 }, 210m)]), None);
        var submitted = await Counts.Submit(count.Id, None);
        submitted.Lines.Single().Expected.Should().BeNull("an opening count has no expected figure");

        await AsAdmin();
        await Counts.Approve(count.Id, None);

        Average("Delmonte").Should().Be(210m);
        OnHand("Delmonte", "Kitchen").Should().Be(24);
        Movements(StockMovementKind.Opening).Should().Contain(m => m.LocationId == Location("Kitchen") && m.Quantity == 24);
        var entry = Entries(StockCountCommands.LedgerSource).Single(e => e.SourceId == count.Id.ToString());
        Dr(entry, LedgerAccountCodes.Stock).Should().Be(5040m);
        Cr(entry, LedgerAccountCodes.OpeningBalances).Should().Be(5040m);
        var again = () => Counts.Start(new StartCountRequest(Location("Kitchen"), StockCountKind.Full, true, null), None);
        await again.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task The_count_sheet_pdf_lists_the_items_without_expected_figures()
    {
        await F.LoginOn("SAM", "Counter B");
        var count = await Counts.Start(new StartCountRequest(Location("Bar"), StockCountKind.Full, false, null), None);

        var pdf = await Counts.SheetPdf(count.Id, None);

        var text = Encoding.Latin1.GetString(pdf);
        text.Should().StartWith("%PDF-1.4").And.EndWith("%%EOF\n");
        text.Should().Contain("Stock count").And.Contain("Tusker Lager").And.Contain("crate").And.Contain("White Cap Lager");
        text.Should().NotContain("Expected").And.NotContain($"({OnHand("Tusker Lager", "Bar"):0})");
    }

    [Fact]
    public async Task Only_one_count_per_location_is_open_at_a_time()
    {
        await CountBar();
        var second = () => Counts.Start(new StartCountRequest(Location("Bar"), StockCountKind.Full, false, null), None);
        await second.Should().ThrowAsync<ConflictException>();
    }
}
