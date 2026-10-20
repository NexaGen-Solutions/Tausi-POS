using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Menu;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;
using Tausi.Domain.People;

namespace Tausi.Tests.Menu;

public class MenuImportTests : IAsyncLifetime
{
    private MenuFixture _f = null!;

    public async Task InitializeAsync() => _f = await MenuFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private static byte[] Utf8(string csv) => Encoding.UTF8.GetBytes(csv);

    private Task<ImportPreview> Preview(byte[] file, ImportOptions? options = null) =>
        _f.Get<MenuImport>().Preview(file, options ?? new ImportOptions(null), CancellationToken.None);

    private Task<ImportResult> Apply(byte[] file, ImportOptions? options = null) =>
        _f.Get<MenuImport>().Apply(file, options ?? new ImportOptions(null), CancellationToken.None);

    private async Task<string> Export() => await _f.Get<ExportMenu>().Execute(CancellationToken.None);

    [Fact]
    public async Task Export_then_import_changes_nothing()
    {
        var preview = await Preview(Utf8(await Export()));

        preview.NeedsMapping.Should().BeFalse();
        preview.Unchanged.Should().Be(13);
        (preview.New, preview.Updated, preview.Errors).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task Export_has_the_agreed_columns()
    {
        var header = (await Export()).Split("\r\n")[0];
        header.Should().Be("category,name,short_name,vat_rate,active,Standard,Price 2,option_groups,erpnext_item_code");
        // Option groups are the item's own; Temperature comes from the Beers category, so it is not repeated.
        (await Export()).Should().Contain("Beers,Tusker Lager,Tusker Lager,,yes,250,,,\r\n");
    }

    [Fact]
    public async Task Two_price_changes_and_one_new_item_preview_and_apply()
    {
        var csv = (await Export())
            .Replace("Beers,Tusker Lager,Tusker Lager,,yes,250,", "Beers,Tusker Lager,Tusker Lager,,yes,260,")
            .Replace("Spirits,Gilbeys 350ml,Gilbeys 350ml,,yes,900,", "Spirits,Gilbeys 350ml,Gilbeys 350ml,,yes,950,")
            + "Food,Chips,Chips,,yes,150,,,\r\n";

        var preview = await Preview(Utf8(csv));
        (preview.New, preview.Updated, preview.Unchanged, preview.Errors).Should().Be((1, 2, 11, 0));
        var tusker = preview.Rows.Single(r => r.Name == "Tusker Lager");
        tusker.Changes.Should().ContainSingle(c => c.IsPrice && c.Old == "250" && c.New == "260");
        preview.PriceChanges.Should().Be(3);

        var result = await Apply(Utf8(csv));

        result.Should().Be(new ImportResult(1, 2, 0, 3, 0));
        _f.Item("Tusker Lager").Prices.Single().Price.Should().Be(260m);
        _f.Item("Chips").Prices.Single().Price.Should().Be(150m);
        var logs = await _f.Read().PriceChangeLogs.ToListAsync();
        logs.Should().HaveCount(3).And.OnlyContain(l => l.Source == PriceChangeSources.Import);
        (await _f.Read().AuditEntries.SingleAsync(a => a.Action == "menu.imported")).NewValueJson
            .Should().Contain("Import: 1 new, 2 updated, 3 prices changed");
    }

    [Fact]
    public async Task SambaPOS_style_columns_map_by_themselves_including_price2()
    {
        var csv = "Category,Name,Price,Price2\r\nBeers,Tusker Lager,250,280\r\nBeers,Tusker Malt,270,290\r\n";

        var preview = await Preview(Utf8(csv));

        preview.NeedsMapping.Should().BeFalse();
        preview.Mapping.Should().Equal("category", "name", $"price:{_f.List("Standard").Id}", $"price:{_f.List("Price 2").Id}");
        await Apply(Utf8(csv));
        _f.Item("Tusker Lager").Prices.Single(p => p.PriceListId == _f.List("Price 2").Id).Price.Should().Be(280m);
        _f.Item("Tusker Malt").Prices.Should().HaveCount(2);
    }

    [Fact]
    public async Task Unknown_headers_need_mapping_and_a_mapping_can_be_given()
    {
        var csv = "Group,Product title,Cost\r\nBeers,Tusker Malt,270\r\n";

        var preview = await Preview(Utf8(csv));
        preview.NeedsMapping.Should().BeTrue();
        preview.Fields.Select(f => f.Field).Should().Contain(["category", "name", $"price:{_f.List("Standard").Id}"]);

        var mapping = new[] { "category", "name", $"price:{_f.List("Standard").Id}" };
        var mapped = await Preview(Utf8(csv), new ImportOptions(mapping));
        mapped.NeedsMapping.Should().BeFalse();
        mapped.New.Should().Be(1);
    }

    [Fact]
    public async Task Existing_items_match_by_name_ignoring_case()
    {
        var preview = await Preview(Utf8("category,name,price\r\nbeers,TUSKER LAGER,255\r\n"));

        preview.Updated.Should().Be(1);
        preview.New.Should().Be(0);
        preview.NewCategories.Should().BeEmpty();
    }

    [Fact]
    public async Task Updating_existing_items_can_be_turned_off()
    {
        var preview = await Preview(Utf8("category,name,price\r\nBeers,Tusker Lager,255\r\n"), new ImportOptions(null, UpdateExisting: false));
        (preview.Updated, preview.Unchanged).Should().Be((0, 1));
    }

    [Fact]
    public async Task Items_missing_from_the_file_can_be_deactivated()
    {
        var csv = "category,name,price\r\nBeers,Tusker Lager,250\r\n";
        var preview = await Preview(Utf8(csv), new ImportOptions(null, DeactivateMissing: true));
        preview.ToDeactivate.Should().HaveCount(12);

        var result = await Apply(Utf8(csv), new ImportOptions(null, DeactivateMissing: true));

        result.Deactivated.Should().Be(12);
        (await _f.Read().MenuItems.CountAsync(i => i.Active)).Should().Be(1);
    }

    [Fact]
    public async Task A_bad_row_stops_the_whole_import_unless_errors_are_skipped()
    {
        var csv = "category,name,price\r\nBeers,Tusker Lager,260\r\nFood,Chips,abc\r\nFood,Samosa,-5\r\n";
        var preview = await Preview(Utf8(csv));
        preview.Errors.Should().Be(2);
        preview.Rows.Single(r => r.Name == "Chips").Error.Should().Contain("not a number");

        await FluentActions.Awaiting(() => Apply(Utf8(csv))).Should().ThrowAsync<DomainException>();
        _f.Item("Tusker Lager").Prices.Single().Price.Should().Be(250m, "nothing was applied");

        var result = await Apply(Utf8(csv), new ImportOptions(null, SkipErrors: true));
        result.Skipped.Should().Be(2);
        _f.Item("Tusker Lager").Prices.Single().Price.Should().Be(260m);
    }

    [Fact]
    public async Task Windows_1252_files_and_thousand_separators_work()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var csv = "category,name,price\r\nFood,Café Crème,\"1,200.00\"\r\nFood,Crêpe,1200\r\n";
        var bytes = Encoding.GetEncoding(1252).GetBytes(csv);

        await Apply(bytes);

        _f.Item("Café Crème").Prices.Single().Price.Should().Be(1200m);
        _f.Item("Crêpe").Prices.Single().Price.Should().Be(1200m);
        _f.Category("Food").Should().NotBeNull();
    }

    [Fact]
    public async Task New_categories_are_listed_and_created()
    {
        var preview = await Preview(Utf8("category,name,price\r\nCocktails,Dawa,450\r\n"));
        preview.NewCategories.Should().Equal("Cocktails");

        await Apply(Utf8("category,name,price\r\nCocktails,Dawa,450\r\n"));
        _f.Item("Dawa").CategoryId.Should().Be(_f.Category("Cocktails").Id);
    }

    [Fact]
    public async Task Changing_prices_by_import_needs_edit_prices()
    {
        await _f.SetPermission("Supervisor", PermissionKeys.MenuEdit, PermissionValue.Allowed);
        _f.ActAs("MAU");

        await FluentActions.Awaiting(() => Apply(Utf8("category,name,price\r\nBeers,Tusker Lager,260\r\n")))
            .Should().ThrowAsync<PermissionDeniedException>();
        await Apply(Utf8("category,name,price,short_name\r\nBeers,Tusker Lager,250,Tusker\r\n"));
        _f.Item("Tusker Lager").ShortName.Should().Be("Tusker");
    }

    [Theory]
    [InlineData("1,200.00", 1200)]
    [InlineData("1200", 1200)]
    [InlineData("KSh 250", 250)]
    [InlineData(" 99.5 ", 99.5)]
    public void Money_parses(string text, decimal expected)
    {
        MenuCsv.TryMoney(text, out var value).Should().BeTrue();
        value.Should().Be(expected);
    }

    [Fact]
    public void Quoted_fields_and_semicolons_parse()
    {
        MenuCsv.Parse("a;b\r\n\"x;y\";\"he said \"\"hi\"\"\"\n").Should().BeEquivalentTo(new[]
        {
            new List<string> { "a", "b" }, new List<string> { "x;y", "he said \"hi\"" },
        });
    }
}
