using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tausi.Application.Auditing;
using Tausi.Application.Events;
using Tausi.Application.Menu;
using Tausi.Application.Setup;
using Tausi.Application.Stock;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;
using Tausi.Domain.Stock;
using Tausi.Tests.Tickets;

namespace Tausi.Tests.Setup;

/// <summary>Menu import from the template, Excel and SambaPOS, and the stock helper.</summary>
public class ImportTests : ApprovalsFixtureBase
{
    private static readonly string Header = string.Join(",", MenuImport.TemplateColumns);

    private async Task Admin() => await F.LoginOn("Administrator", "Counter B");

    private static byte[] Csv(params string[] rows) => Encoding.UTF8.GetBytes(Header + "\r\n" + string.Join("\r\n", rows) + "\r\n");

    private Task<ImportPreview> Preview(byte[] file, ImportOptions? o = null) =>
        F.Get<MenuImport>().Preview(file, o ?? new ImportOptions(null, UpdateExisting: false, SkipErrors: true), None);

    private Task<ImportResult> Apply(byte[] file, ImportOptions? o = null) =>
        F.Get<MenuImport>().Apply(file, o ?? new ImportOptions(null, UpdateExisting: false, SkipErrors: true), None);

    // ---- CSV and Excel ----

    [Fact]
    public async Task The_template_maps_itself_and_imports_with_a_second_price_list()
    {
        await Admin();
        Encoding.UTF8.GetString(MenuImport.Template()).TrimStart('﻿').Should().StartWith(Header);
        var preview = await Preview(MenuImport.Template());
        preview.NeedsMapping.Should().BeFalse();
        preview.Mapping.Should().Contain(m => m == ImportFields.OptionGroups).And.Contain(m => m == ImportFields.Category);
        preview.Mapping[4].Should().Be(ImportFields.PricePrefix + F.Read().PriceLists.Single(l => l.Name == PriceList.Price2Name).Id);

        F.Events.Published.Clear();
        var result = await Apply(MenuImport.Template());
        result.New.Should().Be(2);
        var tusker = F.Read().MenuItems.Include(i => i.Prices).Single(i => i.Name == "Tusker Lager 500ml");
        tusker.Prices.Select(p => p.Price).Should().BeEquivalentTo([250m, 230m]);
        F.Read().ModifierGroupAssignments.Should().Contain(a => a.MenuItemId == tusker.Id);
        F.Events.Published.Should().Contain(e => e.Name == PosEventNames.MenuChanged);
    }

    [Fact]
    public async Task Each_bad_row_says_why_and_is_skipped_with_a_file_of_the_skipped_rows()
    {
        await Admin();
        var file = Csv(
            "Cocktails,Mango Sunrise,Mango,450,,16,no,,,yes",
            "Cocktails,,Nameless,450,,16,no,,,yes",
            "Cocktails,Passion Punch,Punch,abc,,16,no,,,yes",
            "Cocktails,Very long short name,This short name is far too long,300,,16,no,,,yes",
            "Cocktails,Mango Sunrise,Again,450,,16,no,,,yes",
            "Cocktails,Kiwi Cooler,Kiwi,300,,15,no,,,yes",
            "Beers,Tusker Lager,Tusker,250,,16,no,,,yes");
        var preview = await Preview(file);
        var errors = preview.Rows.Where(r => r.Status == ImportStatus.Error).ToDictionary(r => r.Line, r => r.Error);
        errors[3].Should().Be("The name is missing.");
        errors[4].Should().StartWith("The Standard price \"abc\" is not a number");
        errors[5].Should().Be("The short name is over 18 characters. Suggested: \"This short name is\".");
        errors[6].Should().Be("This name is in this category twice.");
        errors[7].Should().StartWith("Unknown VAT rate 15%");
        (preview.New, preview.Errors, preview.Existing).Should().Be((1, 5, 1), "1 ready, 5 with errors, 1 duplicate of an existing item");

        var skipped = Encoding.UTF8.GetString(await F.Get<MenuImport>().SkippedRows(file, new ImportOptions(null, UpdateExisting: false), None)).TrimStart('﻿');
        var lines = skipped.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be(Header + ",reason");
        lines.Should().HaveCount(6);
        lines[1].Should().StartWith("Cocktails,,Nameless").And.EndWith("The name is missing.");

        var result = await Apply(file);
        (result.New, result.Skipped, result.Existing).Should().Be((1, 5, 1));
        F.Read().MenuItems.Count(i => i.Name == "Mango Sunrise").Should().Be(1);
    }

    [Fact]
    public async Task Importing_again_never_duplicates_and_updates_only_when_asked()
    {
        await Admin();
        var file = Csv("Cocktails,Mango Sunrise,Mango,450,,16,no,,,yes");
        await Apply(file);
        var count = F.Read().MenuItems.Count();
        (await Apply(file)).New.Should().Be(0);
        F.Read().MenuItems.Count().Should().Be(count);

        var dearer = Csv("Cocktails,Mango Sunrise,Mango,500,,16,no,,,yes");
        (await Apply(dearer)).Updated.Should().Be(0, "\"Update existing items\" is off");
        (await Apply(dearer, new ImportOptions(null, UpdateExisting: true, SkipErrors: true))).Updated.Should().Be(1);
        F.Read().MenuItemPrices.Single(p => p.MenuItemId == F.Item("Mango Sunrise")).Price.Should().Be(500);
    }

    [Fact]
    public async Task An_excel_file_imports_like_a_csv()
    {
        await Admin();
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Menu");
        for (var c = 0; c < MenuImport.TemplateColumns.Count; c++) sheet.Cell(1, c + 1).Value = MenuImport.TemplateColumns[c];
        sheet.Cell(2, 1).Value = "Cocktails";
        sheet.Cell(2, 2).Value = "Dawa";
        sheet.Cell(2, 4).Value = 350;
        sheet.Cell(2, 6).Value = 16;
        using var stream = new MemoryStream();
        book.SaveAs(stream);

        var result = await Apply(stream.ToArray());
        result.New.Should().Be(1);
        F.Read().MenuItemPrices.Single(p => p.MenuItemId == F.Item("Dawa")).Price.Should().Be(350);
    }

    [Fact]
    public async Task A_failure_part_way_imports_nothing()
    {
        await Admin();
        var before = F.Read().MenuItems.Count();
        await using var failing = F.NewContext(new FailingSave());
        var import = new MenuImport(failing, F.Get<MenuAccess>(), new AuditLog(failing, F.Session, F.Clock), F.Clock);
        await FluentActions.Awaiting(() => import.Apply(Csv("Cocktails,Mango Sunrise,Mango,450,,16,no,,,yes", "Cocktails,Dawa,Dawa,350,,16,no,,,yes"),
            new ImportOptions(null, SkipErrors: true), None)).Should().ThrowAsync<InvalidOperationException>();
        F.Read().MenuItems.Count().Should().Be(before);
        F.Read().Categories.Should().NotContain(c => c.Name == "Cocktails");
    }

    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("The disk is full.");
    }

    // ---- SambaPOS (the mapping; the reader itself is tested on SQL Server) ----

    private void SambaData() => F.SambaPos.Data = new SambaPosData(
        [new(1, "Tusker", "Beers"), new(2, "Guinness Can", "Beers"), new(3, "Chips", null)],
        [new(11, 1, "Normal"), new(21, 2, "Small"), new(22, 2, "Large"), new(31, 3, "Normal")],
        [new(11, null, 250), new(11, "Happy Hour", 200), new(21, null, 200), new(22, null, 350), new(31, null, 150)],
        [new(5, "Temperature", 1, 1), new(6, "Sauce", 0, 2)],
        [new(51, 5, "Cold", 0), new(52, 5, "Warm", 0), new(61, 6, "Chilli", 10)],
        [new(5, null, "Beers"), new(6, 3, null)],
        [new(101, "Garden 1"), new(102, "Garden 2")],
        [new(201, "Jane", "Waiter"), new(202, "Bob", "Manager"), new(203, "Zed", "Cleaner"), new(204, "edna", "Waiter")],
        [],
        ["OrderTagMaps: no TerminalId column, skipped."]);

    private static readonly SambaImportRequest Everything = new(new SambaPosConnection("POSSERVER2\\SQLEXPRESS", "SambaPOS5", true, null, null), null);

    [Fact]
    public async Task SambaPos_items_portions_price_tags_order_tags_tables_and_users_come_across_once()
    {
        await Admin();
        SambaData();
        var import = F.Get<SambaPosImport>();
        var preview = await import.Preview(Everything, None);
        preview.Items.Select(i => i.Name).Should().Equal("Tusker", "Guinness Can (Small)", "Guinness Can (Large)", "Chips");
        preview.Items.Single(i => i.Name == "Chips").Category.Should().Be("Other");
        preview.PriceLists.Should().Equal("Happy Hour", "Standard");
        preview.ModifierGroups.Should().Contain("Temperature (Cold / Warm)");
        preview.Tables.Should().Equal("Garden 1", "Garden 2");
        preview.Users.Single(u => u.Name == "Bob").Role.Should().Be("supervisor");
        preview.Users.Single(u => u.Name == "Zed").Status.Should().Be("Choose a role");
        preview.Users.Single(u => u.Name == "edna").Status.Should().Be("Existing");
        preview.Problems.Should().ContainSingle();

        var result = await import.Apply(Everything with { Options = new SambaImportOptions(UserRoles: new Dictionary<string, string> { ["203"] = "waiter" }) }, None);
        result.Summary.Should().StartWith("Imported 4 items");
        result.People.Select(p => p.Name).Should().BeEquivalentTo(["Jane", "Bob", "Zed"]);
        System.Text.Encoding.Latin1.GetString(Convert.FromBase64String(result.PinSheetPdf!)).Should().StartWith("%PDF");

        var db = F.Read();
        var large = db.MenuItems.Include(i => i.Prices).Single(i => i.Name == "Guinness Can (Large)");
        large.ExternalRef.Should().Be("sambapos:portion:22");
        large.Prices.Single().Price.Should().Be(350);
        var happy = db.PriceLists.Single(l => l.Name == "Happy Hour");
        db.MenuItemPrices.Single(p => p.PriceListId == happy.Id).Price.Should().Be(200);
        db.ModifierGroups.Count(g => g.Name == "Temperature").Should().Be(1, "matched by name, not duplicated");
        db.ModifierGroupAssignments.Should().Contain(a => a.CategoryId == db.Categories.Single(c => c.Name == "Beers").Id);
        db.ModifierGroups.Include(g => g.Options).Single(g => g.Name == "Sauce").Options.Single().Should()
            .Match<ModifierOption>(o => o.Name == "Chilli" && o.PriceAddOn == 10);
        db.ModifierGroupAssignments.Should().Contain(a => a.MenuItemId == db.MenuItems.Single(i => i.Name == "Chips").Id);
        db.Tables.Include(t => t.Area).Where(t => t.Name.StartsWith("Garden")).Should().OnlyContain(t => t.Area!.Name == "Main");
        db.Users.Where(u => u.ExternalRef != null).Should().HaveCount(3).And.OnlyContain(u => u.MustChangePin);

        var again = await import.Preview(Everything, None);
        (again.NewItems, again.NewTables, again.NewUsers).Should().Be((0, 0, 0));
        var counts = (db.MenuItems.Count(), db.Tables.Count(), db.Users.Count(), db.PriceLists.Count());
        await import.Apply(Everything, None);
        var after = F.Read();
        (after.MenuItems.Count(), after.Tables.Count(), after.Users.Count(), after.PriceLists.Count()).Should().Be(counts);
    }

    // ---- Stock ----

    [Fact]
    public async Task Bottled_menu_items_get_a_one_to_one_stock_item_and_recipe()
    {
        await Admin();
        var beers = F.Read().Categories.Single(c => c.Name == "Beers").Id;
        F.Db.MenuItems.Add(new MenuItem { Name = "Faxe Strong Can", ShortName = "Faxe Strong", CategoryId = beers });
        F.Db.MenuItems.Add(new MenuItem { Name = "Gilbeys Double Tot", ShortName = "Gilbeys Dbl", CategoryId = beers });
        await F.Db.SaveChangesAsync();

        var suggestions = await F.Get<StockFromMenu>().Suggestions(None);
        var faxe = suggestions.Single(s => s.MenuItem == "Faxe Strong Can");
        (faxe.Suggested, faxe.StockCategory, faxe.BaseUnit, faxe.PackName, faxe.PackFactor).Should().Be((true, "Beer", "Can", "tray", 24m));
        suggestions.Single(s => s.MenuItem == "Gilbeys Double Tot").Should().Match<StockSuggestion>(s => !s.Suggested && s.Hint!.StartsWith("Sold in tots"));

        (await F.Get<StockFromMenu>().Create(new CreateFromMenuRequest([faxe with { PackFactor = 12 }]), None)).Should().Be(1);
        var item = F.Read().StockItems.Single(i => i.Name == "Faxe Strong Can");
        item.Packs.Single().Should().Be(new StockPack("tray", 12));
        F.Read().RecipeLines.Single(r => r.StockItemId == item.Id).BaseQuantity.Should().Be(1);
    }

    [Fact]
    public async Task During_setup_only_the_owner_approves_their_own_opening_count()
    {
        await Admin();
        var cellar = await F.Get<StockLocations>().Save(null, new SaveLocationRequest("Cellar", true, null), None);
        var counts = F.Get<StockCountCommands>();
        async Task<Guid> Submitted()
        {
            var count = await counts.Start(new StartCountRequest(cellar.Id, StockCountKind.Full, true, null), None);
            await counts.Enter(count.Id, new EnterCountsRequest(count.Lines.Select(l => new CountEntry(l.StockItemId, new() { [l.BaseUnit] = 2 }, 50)).ToList()), None);
            await counts.Submit(count.Id, None);
            return count.Id;
        }

        var first = await Submitted();
        await FluentActions.Awaiting(() => counts.Approve(first, None)).Should().ThrowAsync<Application.Common.ForbiddenException>("after setup, someone else approves");
        await F.Db.SetupStates.ExecuteUpdateAsync(u => u.SetProperty(s => s.CompletedAt, (DateTimeOffset?)null));
        (await counts.Approve(first, None)).Status.Should().Be("Approved");
        F.Read().AuditEntries.Should().Contain(a => a.Action == AuditActions.StockCountChanged && a.Actor == SetupFlow.SetupActor);
    }

    [Fact]
    public async Task Locations_are_added_renamed_and_switched_off_but_not_while_a_terminal_sells_from_one()
    {
        await Admin();
        var locations = F.Get<StockLocations>();
        var cellar = await locations.Save(null, new SaveLocationRequest("Cellar", true, null), None);
        (await locations.Save(cellar.Id, new SaveLocationRequest("Back store", false, cellar.RowVersion), None)).Name.Should().Be("Back store");
        await FluentActions.Awaiting(() => locations.Save(null, new SaveLocationRequest("Bar", true, null), None))
            .Should().ThrowAsync<Application.Common.FieldErrorsException>();
        var bar = (await locations.List(None)).Single(l => l.Name == "Bar");
        await FluentActions.Awaiting(() => locations.Save(bar.Id, new SaveLocationRequest("Bar", false, bar.RowVersion), None))
            .Should().ThrowAsync<DomainException>();
    }
}
