using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Menu;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;
using Tausi.Domain.Money;
using Tausi.Domain.People;

namespace Tausi.Tests.Menu;

public class MenuTests : IAsyncLifetime
{
    private MenuFixture _f = null!;

    public async Task InitializeAsync() => _f = await MenuFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Guid Standard => _f.List("Standard").Id;
    private Guid Price2 => _f.List("Price 2").Id;

    private Task<int> SetPrices(string item, Dictionary<Guid, decimal?> prices, bool allowZero = false) =>
        _f.Get<SetItemPrices>().Execute(_f.Item(item).Id, new ItemPricesRequest(prices, allowZero, _f.Item(item).RowVersion), CancellationToken.None);

    private Task<SavedRef> Rename(string item, string newName)
    {
        var i = _f.Item(item);
        return _f.Get<SaveItem>().Execute(i.Id, new ItemRequest(newName, null, i.CategoryId, i.VatRate, i.Active, i.ErpItemCode, null, null, i.RowVersion), CancellationToken.None);
    }

    private Task<PosMenuDto> PosMenu(Tausi.Domain.Devices.Terminal terminal)
    {
        _f.OnTerminal(terminal);
        return _f.Get<GetPosMenu>().Execute(CancellationToken.None);
    }

    private static PosItemDto PosItem(PosMenuDto menu, string name) => menu.Categories.SelectMany(c => c.Items).Single(i => i.Name == name);

    // ---- Seed ----

    [Fact]
    public async Task The_demo_menu_has_the_categories_items_prices_and_temperature()
    {
        var menu = await _f.Get<GetAdminMenu>().Execute(CancellationToken.None);

        menu.Categories.Select(c => c.Name).Should().Equal("Beers", "Spirits", "Soft drinks", "Water & juice", "Pool", "Food");
        menu.Items.Should().HaveCount(13);
        menu.PriceLists.Select(l => (l.Name, l.IsDefault)).Should().Equal(("Standard", true), ("Price 2", false));
        _f.Item("Tusker Lager").Prices.Single().Price.Should().Be(250m);
        _f.Item("Delmonte").Prices.Single().Price.Should().Be(400m);

        var tusker = PosItem(await PosMenu(_f.CounterA), "Tusker Lager");
        tusker.Groups.Should().ContainSingle(g => g.Name == "Temperature" && g.MinSelect == 1 && g.MaxSelect == 1);
        tusker.Groups[0].Options.Select(o => o.Name).Should().Equal("Cold", "Warm");
        PosItem(await PosMenu(_f.CounterA), "Viceroy 375ml").Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task Items_per_page_is_from_4_to_60_or_empty_and_reaches_the_terminal_menu()
    {
        var beers = _f.Category("Beers");
        var save = _f.Get<SaveCategory>();

        var tooFew = () => save.Execute(beers.Id, new CategoryRequest("Beers", true, beers.RowVersion, 3), CancellationToken.None);
        (await tooFew.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(MenuCodes.ItemsPerPage);
        var saved = await save.Execute(beers.Id, new CategoryRequest("Beers", true, beers.RowVersion, 12), CancellationToken.None);

        saved.ItemsPerPage.Should().Be(12);
        (await PosMenu(_f.CounterA)).Categories.Single(c => c.Name == "Beers").ItemsPerPage.Should().Be(12);
        (await PosMenu(_f.CounterA)).Categories.Single(c => c.Name == "Spirits").ItemsPerPage.Should().BeNull("one scrolling page");
    }

    // ---- Permissions ----

    [Fact]
    public async Task Menu_edit_without_edit_prices_can_rename_but_not_price()
    {
        await _f.SetPermission("Supervisor", PermissionKeys.MenuEdit, PermissionValue.Allowed);
        _f.ActAs("SAM");

        await Rename("Tusker Lager", "Tusker Lager 500ml");
        var view = await _f.Get<GetAdminMenu>().Execute(CancellationToken.None);
        view.CanEdit.Should().BeTrue();
        view.CanEditPrices.Should().BeFalse();

        await FluentActions.Awaiting(() => SetPrices("Tusker Lager 500ml", new() { [Standard] = 260m }))
            .Should().ThrowAsync<PermissionDeniedException>();
        _f.Item("Tusker Lager 500ml").Prices.Single().Price.Should().Be(250m);
    }

    [Theory]
    [InlineData("""{"name":"X","shortName":null,"categoryId":"00000000-0000-0000-0000-000000000000","vatRate":null,"active":true,"erpItemCode":null,"groupIds":null,"prices":{"a":1}}""")]
    [InlineData("""{"name":"X","shortName":null,"categoryId":"00000000-0000-0000-0000-000000000000","vatRate":null,"active":true,"erpItemCode":null,"groupIds":null,"price":260}""")]
    public void Item_requests_refuse_price_fields(string json)
    {
        var act = () => JsonSerializer.Deserialize<ItemRequest>(json, Json.Options);
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Option_group_requests_refuse_add_ons()
    {
        var json = """{"name":"T","minSelect":1,"maxSelect":1,"sortOrder":0,"active":true,"options":[{"id":null,"name":"Cold","sortOrder":0,"priceAddOn":20}],"categoryIds":[],"itemIds":[]}""";
        var act = () => JsonSerializer.Deserialize<GroupRequest>(json, Json.Options);
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public async Task Nobody_without_either_menu_permission_sees_the_menu_admin()
    {
        _f.ActAs("SAM");
        await FluentActions.Awaiting(() => _f.Get<GetAdminMenu>().Execute(CancellationToken.None))
            .Should().ThrowAsync<PermissionDeniedException>();
    }

    [Fact]
    public async Task Edit_prices_can_not_be_approved_so_needs_approval_counts_as_not_allowed()
    {
        await _f.SetPermission("Supervisor", PermissionKeys.MenuEditPrices, PermissionValue.NeedsApproval);
        _f.ActAs("SAM", _f.CounterA);
        var token = SecureTokens.New();
        _f.Db.ApprovalGrants.Add(new ApprovalGrant
        {
            TokenHash = SecureTokens.Hash(token), PermissionKey = PermissionKeys.MenuEditPrices,
            ApprovedByUserId = _f.Users["Administrator"].Id, CreatedAt = _f.Clock.GetUtcNow(), ExpiresAt = _f.Clock.GetUtcNow().AddSeconds(60),
        });
        await _f.Db.SaveChangesAsync();
        _f.ApprovalToken = token;

        await FluentActions.Awaiting(() => SetPrices("Heineken", new() { [Standard] = 320m }))
            .Should().ThrowAsync<PermissionDeniedException>();
        (await _f.Read().PriceChangeLogs.CountAsync()).Should().Be(0);
    }

    // ---- Prices ----

    [Fact]
    public async Task A_price_change_is_logged_and_announced()
    {
        (await SetPrices("Tusker Lager", new() { [Standard] = 260m, [Price2] = 280m })).Should().Be(2);

        var logs = await _f.Read().PriceChangeLogs.OrderBy(l => l.NewPrice).ToListAsync();
        logs.Select(l => (l.OldPrice, l.NewPrice, l.Source)).Should().Equal(((decimal?)250m, (decimal?)260m, "admin"), (null, 280m, "admin"));
        logs.Should().OnlyContain(l => l.ChangedByUserId == _f.Users["Administrator"].Id && l.TerminalId == null);
        _f.Events.Published.Should().Contain(e => e.Name == PosEventNames.MenuChanged);

        var entries = await _f.Get<GetPriceLog>().Execute(_f.Item("Tusker Lager").Id, 10, CancellationToken.None);
        entries.Should().Contain(e => e.OldPrice == 250m && e.NewPrice == 260m && e.Where == "Admin site" && e.ChangedBy == "Administrator");
    }

    [Fact]
    public async Task Unchanged_prices_write_nothing()
    {
        (await SetPrices("Tusker Lager", new() { [Standard] = 250m })).Should().Be(0);
        (await _f.Read().PriceChangeLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Negative_prices_are_refused_and_zero_needs_confirmation()
    {
        (await FluentActions.Awaiting(() => SetPrices("Tusker Lager", new() { [Standard] = -1m })).Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(MenuCodes.PriceNegative);
        (await FluentActions.Awaiting(() => SetPrices("Tusker Lager", new() { [Standard] = 0m })).Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(MenuCodes.PriceZero);

        await SetPrices("Tusker Lager", new() { [Standard] = 0m }, allowZero: true);
        _f.Item("Tusker Lager").Prices.Single().Price.Should().Be(0m);
    }

    [Fact]
    public async Task The_default_list_price_cannot_be_removed()
    {
        (await FluentActions.Awaiting(() => SetPrices("Tusker Lager", new() { [Standard] = null })).Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(MenuCodes.DefaultPriceRequired);
    }

    [Fact]
    public async Task A_stale_price_edit_gets_a_conflict()
    {
        var old = _f.Item("Tusker Lager").RowVersion;
        await SetPrices("Tusker Lager", new() { [Standard] = 260m });

        await FluentActions.Awaiting(() => _f.Get<SetItemPrices>().Execute(_f.Item("Tusker Lager").Id,
                new ItemPricesRequest(new Dictionary<Guid, decimal?> { [Standard] = 270m }, false, old), CancellationToken.None))
            .Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public void Net_and_vat_for_250_at_16_percent()
    {
        VatCalculator.ForLine(250m, 1, 16m).Should().Be(new LineAmounts(250m, 215.52m, 34.48m));
    }

    // ---- Price lists ----

    [Fact]
    public async Task There_is_only_ever_one_default_list()
    {
        await SetPrices("Tusker Lager", new() { [Price2] = 280m });
        foreach (var name in _f.Read().MenuItems.Where(i => i.Name != "Tusker Lager").Select(i => i.Name).ToList())
            await SetPrices(name, new() { [Price2] = 1m });

        await _f.Get<MakeDefaultPriceList>().Execute(Price2, CancellationToken.None);

        (await _f.Read().PriceLists.Where(l => l.IsDefault).Select(l => l.Name).ToListAsync()).Should().Equal("Price 2");

        _f.Db.PriceLists.Add(new PriceList { Name = "Third", IsDefault = true });
        await FluentActions.Awaiting(() => _f.Db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task A_list_missing_prices_cannot_become_default_and_the_default_cannot_be_deactivated()
    {
        await FluentActions.Awaiting(() => _f.Get<MakeDefaultPriceList>().Execute(Price2, CancellationToken.None))
            .Should().ThrowAsync<DomainException>();
        var standard = _f.List("Standard");
        await FluentActions.Awaiting(() => _f.Get<SavePriceList>().Execute(standard.Id, new PriceListRequest("Standard", false, standard.RowVersion), CancellationToken.None))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task A_terminals_list_overrides_the_default_and_missing_prices_fall_back()
    {
        await SetPrices("Tusker Lager", new() { [Price2] = 280m });
        await _f.Get<Tausi.Application.Terminals.SetTerminalPriceList>().Execute(
            _f.CounterB.Id, new Tausi.Application.Terminals.TerminalPriceListRequest(Price2), CancellationToken.None);

        var b = await PosMenu(_f.CounterB);
        b.PriceListName.Should().Be("Price 2");
        PosItem(b, "Tusker Lager").Price.Should().Be(280m);
        PosItem(b, "Guiness").Price.Should().Be(300m, "no Price 2 price: the Standard price is used");
        PosItem(await PosMenu(_f.CounterA), "Tusker Lager").Price.Should().Be(250m);
    }

    // ---- Option groups ----

    [Fact]
    public async Task Items_get_their_categorys_groups_plus_their_own()
    {
        var size = await _f.Get<SaveGroup>().Execute(null, new GroupRequest("Size", 0, 1, 20, true,
            [new OptionRequest(null, "Small", 10), new OptionRequest(null, "Large", 20)], null, [_f.Item("Tusker Lager").Id]), CancellationToken.None);

        var tusker = PosItem(await PosMenu(_f.CounterA), "Tusker Lager");
        tusker.Groups.Select(g => g.Name).Should().Equal("Temperature", "Size");
        PosItem(await PosMenu(_f.CounterA), "Heineken").Groups.Select(g => g.Name).Should().Equal("Temperature");

        var admin = await _f.Get<GetAdminMenu>().Execute(CancellationToken.None);
        var item = admin.Items.Single(i => i.Name == "Tusker Lager");
        item.OwnGroupIds.Should().Equal(size.Id);
        item.InheritedGroupIds.Should().ContainSingle();
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(0, 0)]
    [InlineData(-1, 1)]
    [InlineData(3, 3)]
    public async Task Group_min_and_max_must_make_sense(int min, int max)
    {
        await FluentActions.Awaiting(() => _f.Get<SaveGroup>().Execute(null, new GroupRequest("Bad", min, max, 0, true,
                [new OptionRequest(null, "A", 1), new OptionRequest(null, "B", 2)], null, null), CancellationToken.None))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Option_add_ons_are_prices_and_are_logged()
    {
        var group = await _f.Read().ModifierGroups.Include(g => g.Options).SingleAsync();
        var cold = group.Options.Single(o => o.Name == "Cold");

        await _f.Get<SetOptionPrices>().Execute(group.Id, new OptionPricesRequest(new Dictionary<Guid, decimal> { [cold.Id] = 20m }, group.RowVersion), CancellationToken.None);

        PosItem(await PosMenu(_f.CounterA), "Tusker Lager").Groups[0].Options.Single(o => o.Name == "Cold").PriceAddOn.Should().Be(20m);
        (await _f.Read().PriceChangeLogs.SingleAsync()).ModifierOptionId.Should().Be(cold.Id);
    }

    // ---- Terminal menu ----

    [Fact]
    public async Task Inactive_items_and_categories_are_not_sold_and_the_version_changes()
    {
        var before = await PosMenu(_f.CounterA);
        var spirits = _f.Category("Spirits");

        var count = await _f.Get<DeactivateCategory>().Execute(spirits.Id, new DeactivateCategoryRequest(true, spirits.RowVersion), CancellationToken.None);
        count.Should().Be(2);
        var after = await PosMenu(_f.CounterA);
        after.Categories.Select(c => c.Name).Should().NotContain("Spirits");
        after.MenuVersion.Should().NotBe(before.MenuVersion);
        _f.Item("Viceroy 375ml").Active.Should().BeFalse();

        (await PosMenu(_f.CounterA)).MenuVersion.Should().Be(after.MenuVersion, "nothing changed since");
        await SetPrices("Tusker Lager", new() { [Standard] = 260m });
        (await PosMenu(_f.CounterA)).MenuVersion.Should().NotBe(after.MenuVersion);
    }

    [Fact]
    public async Task A_new_item_without_a_price_is_not_sold_yet()
    {
        await _f.Get<SaveItem>().Execute(null, new ItemRequest("Chips", null, _f.Category("Food").Id, null, true, null, null), CancellationToken.None);

        (await PosMenu(_f.CounterA)).Categories.SelectMany(c => c.Items).Should().NotContain(i => i.Name == "Chips");
        await SetPrices("Chips", new() { [Standard] = 150m });
        PosItem(await PosMenu(_f.CounterA), "Chips").ShortName.Should().Be("Chips");
    }

    [Fact]
    public async Task An_item_is_either_zero_rated_or_vat_exempt()
    {
        var food = _f.Category("Food").Id;
        await _f.Get<SaveItem>().Execute(null, new ItemRequest("Unga 2kg", null, food, 0m, true, null, null), CancellationToken.None);
        await _f.Get<SaveItem>().Execute(null, new ItemRequest("Bottled water", null, food, 16m, true, null, null, VatExempt: true), CancellationToken.None);

        _f.Item("Unga 2kg").Should().Match<Tausi.Domain.Menu.MenuItem>(i => i.VatRate == 0m && !i.VatExempt);
        _f.Item("Bottled water").Should().Match<Tausi.Domain.Menu.MenuItem>(i => i.VatRate == 0m && i.VatExempt, "an exempt item keeps no rate");
        _f.Item("Bottled water").EffectiveVatRate(16m).Should().Be(0m);
    }

    [Fact]
    public async Task Short_names_default_to_18_characters_and_are_checked()
    {
        var food = _f.Category("Food").Id;
        await _f.Get<SaveItem>().Execute(null, new ItemRequest("Grilled chicken and chips basket", null, food, 0m, true, null, null), CancellationToken.None);
        _f.Item("Grilled chicken and chips basket").ShortName.Should().Be("Grilled chicken an");
        _f.Item("Grilled chicken and chips basket").VatRate.Should().Be(0m);

        (await FluentActions.Awaiting(() => _f.Get<SaveItem>().Execute(null,
                new ItemRequest("Fish", "A short name too long", food, null, true, null, null), CancellationToken.None))
            .Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(MenuCodes.ShortNameTooLong);
        (await FluentActions.Awaiting(() => _f.Get<SaveItem>().Execute(null,
                new ItemRequest("TUSKER LAGER", null, food, null, true, null, null), CancellationToken.None))
            .Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(MenuCodes.NameTaken);
    }

    [Fact]
    public async Task Effective_vat_is_the_items_own_or_the_business_default()
    {
        var food = _f.Category("Food").Id;
        await _f.Get<SaveItem>().Execute(null, new ItemRequest("Bread", null, food, 0m, true, null, null), CancellationToken.None);
        await SetPrices("Bread", new() { [Standard] = 50m });

        PosItem(await PosMenu(_f.CounterA), "Bread").VatRate.Should().Be(0m);
        PosItem(await PosMenu(_f.CounterA), "Tusker Lager").VatRate.Should().Be(16m);
    }
}
