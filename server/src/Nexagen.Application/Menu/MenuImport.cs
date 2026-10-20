using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Auditing;
using Nexagen.Application.Common;
using Nexagen.Domain.Common;
using Nexagen.Domain.Menu;

namespace Nexagen.Application.Menu;

/// <summary>Fields a CSV column can fill. Prices are "price:&lt;price list id&gt;".</summary>
public static class ImportFields
{
    public const string Ignore = "ignore";
    public const string Category = "category";
    public const string Name = "name";
    public const string ShortName = "short_name";
    public const string VatRate = "vat_rate";
    public const string Active = "active";
    public const string OptionGroups = "option_groups";
    public const string ErpItemCode = "erpnext_item_code";
    public const string PricePrefix = "price:";
}

public sealed record ImportOptions(
    IReadOnlyList<string>? Mapping,
    bool UpdateExisting = true,
    bool DeactivateMissing = false,
    bool SkipErrors = false);

public sealed record ImportFieldChoice(string Field, string Label);

public sealed record ImportChange(string Field, string? Old, string? New, bool IsPrice);

public sealed record ImportRow(int Line, string Status, string Name, string Category, IReadOnlyList<ImportChange> Changes, string? Error);

public sealed record ImportPreview(
    IReadOnlyList<string> Headers,
    IReadOnlyList<string> Mapping,
    IReadOnlyList<ImportFieldChoice> Fields,
    bool NeedsMapping,
    string? MappingProblem,
    IReadOnlyList<ImportRow> Rows,
    IReadOnlyList<string> NewCategories,
    IReadOnlyList<string> ToDeactivate,
    int New,
    int Updated,
    int Unchanged,
    int Errors,
    int PriceChanges);

public sealed record ImportResult(int New, int Updated, int Deactivated, int PriceChanges, int Skipped);

public static class ImportStatus
{
    public const string New = "New";
    public const string Updated = "Updated";
    public const string Unchanged = "Unchanged";
    public const string Error = "Error";
}

/// <summary>
/// Menu import from CSV (SambaPOS exports or our own export). Preview first: what is new, updated
/// (old → new), unchanged, or wrong, and which categories will be created. Apply does the same in
/// one transaction, logging price changes with source "import".
/// </summary>
public sealed class MenuImport(INexagenDb db, MenuAccess access, IAuditLog audit, TimeProvider clock)
{
    public async Task<ImportPreview> Preview(byte[] file, ImportOptions options, CancellationToken ct)
    {
        await access.RequireEdit(ct);
        return (await Plan(file, options, ct)).Preview;
    }

    public async Task<ImportResult> Apply(byte[] file, ImportOptions options, CancellationToken ct)
    {
        var grant = await access.RequireEdit(ct);
        var plan = await Plan(file, options, ct);
        var preview = plan.Preview;
        if (preview.NeedsMapping)
            throw new DomainException(preview.MappingProblem ?? "Match the columns first.", MenuCodes.ImportErrors);
        if (preview.Errors > 0 && !options.SkipErrors)
            throw new DomainException($"{preview.Errors} rows have errors. Fix them, or choose to skip rows with errors.", MenuCodes.ImportErrors);
        var priceGrant = preview.PriceChanges > 0 ? await access.RequirePrices(ct) : null;

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var lastCategory = await db.Categories.MaxAsync(c => (int?)c.SortOrder, ct) ?? 0;
        foreach (var name in preview.NewCategories)
        {
            var category = new Category { Name = name, SortOrder = lastCategory += 10 };
            db.Categories.Add(category);
            plan.Categories[name.ToUpperInvariant()] = category;
        }

        foreach (var row in plan.Work)
        {
            var item = row.Existing;
            if (item is null)
            {
                item = new MenuItem { Name = row.Name, SortOrder = (plan.LastSort.GetValueOrDefault(row.CategoryKey) + 10) };
                plan.LastSort[row.CategoryKey] = item.SortOrder;
                db.MenuItems.Add(item);
            }
            item.CategoryId = plan.Categories[row.CategoryKey].Id;
            if (row.ShortName is not null) item.ShortName = row.ShortName;
            else if (row.Existing is null) item.ShortName = MenuItem.DefaultShortName(row.Name);
            if (row.HasVat) item.VatRate = row.Vat;
            if (row.Active is { } active) item.Active = active;
            if (row.ErpCode is not null) item.ErpItemCode = row.ErpCode.Length == 0 ? null : row.ErpCode;

            foreach (var (listId, price) in row.Prices)
            {
                var existing = item.Prices.FirstOrDefault(p => p.PriceListId == listId);
                if (existing?.Price == price) continue;
                if (existing is null) db.MenuItemPrices.Add(new MenuItemPrice { MenuItemId = item.Id, PriceListId = listId, Price = price });
                else existing.Price = price;
                db.PriceChangeLogs.Add(new PriceChangeLog
                {
                    MenuItemId = item.Id, PriceListId = listId, OldPrice = existing?.Price, NewPrice = price,
                    ChangedByUserId = access.User.UserId, ApprovedByUserId = priceGrant?.ApprovedByUserId,
                    TerminalId = access.TerminalId, Source = PriceChangeSources.Import, At = now,
                });
            }

            if (row.GroupIds is { } groupIds)
            {
                var current = row.Existing is null ? [] : plan.Assignments.Where(a => a.MenuItemId == item.Id).ToList();
                db.ModifierGroupAssignments.RemoveRange(current.Where(a => !groupIds.Contains(a.GroupId)));
                foreach (var g in groupIds.Where(g => current.All(a => a.GroupId != g)))
                    db.ModifierGroupAssignments.Add(new ModifierGroupAssignment { GroupId = g, MenuItemId = item.Id });
            }
        }

        foreach (var item in plan.ToDeactivate) item.Active = false;

        var result = new ImportResult(preview.New, preview.Updated, plan.ToDeactivate.Count, preview.PriceChanges, preview.Errors);
        audit.Write(new AuditRecord(AuditActions.MenuImported, null, null, null, new
        {
            summary = $"Import: {result.New} new, {result.Updated} updated, {result.PriceChanges} prices changed"
                + (result.Deactivated > 0 ? $", {result.Deactivated} deactivated" : "")
                + (result.Skipped > 0 ? $", {result.Skipped} rows skipped" : ""),
            newCategories = preview.NewCategories,
        }, priceGrant?.ApprovedByUserId ?? grant.ApprovedByUserId));

        await MenuAccess.Save(db, ct);
        await tx.CommitAsync(ct);
        await access.Changed(ct);
        return result;
    }

    // ---- Planning (shared by preview and apply) ----

    private sealed class WorkRow
    {
        public required string Name { get; init; }
        public required string CategoryKey { get; init; }
        public MenuItem? Existing { get; init; }
        public string? ShortName { get; set; }
        public bool HasVat { get; set; }
        public decimal? Vat { get; set; }
        public bool? Active { get; set; }
        public string? ErpCode { get; set; }
        public List<Guid>? GroupIds { get; set; }
        public Dictionary<Guid, decimal> Prices { get; } = [];
    }

    private sealed record PlanResult(
        ImportPreview Preview,
        List<WorkRow> Work,
        Dictionary<string, Category> Categories,
        Dictionary<string, int> LastSort,
        List<ModifierGroupAssignment> Assignments,
        List<MenuItem> ToDeactivate);

    private async Task<PlanResult> Plan(byte[] file, ImportOptions options, CancellationToken ct)
    {
        var rows = MenuCsv.Parse(MenuCsv.Decode(file));
        if (rows.Count == 0) throw new DomainException("The file is empty.", MenuCodes.ImportErrors);

        var lists = await db.PriceLists.AsNoTracking().OrderByDescending(l => l.IsDefault).ThenBy(l => l.Name).ToListAsync(ct);
        var defaultList = lists.First(l => l.IsDefault);
        var categories = (await db.Categories.ToListAsync(ct)).ToDictionary(c => c.Name.ToUpperInvariant());
        var items = (await db.MenuItems.Include(i => i.Prices).ToListAsync(ct)).ToDictionary(i => i.Name.ToUpperInvariant());
        var groups = (await db.ModifierGroups.AsNoTracking().ToListAsync(ct)).ToDictionary(g => g.Name.ToUpperInvariant());
        var assignments = await db.ModifierGroupAssignments.Where(a => a.MenuItemId != null).ToListAsync(ct);
        var categoryNames = categories.Values.ToDictionary(c => c.Id, c => c.Name);
        var groupNames = groups.Values.ToDictionary(g => g.Id, g => g.Name);

        var fields = new List<ImportFieldChoice>
        {
            new(ImportFields.Ignore, "Ignore"), new(ImportFields.Category, "Category"), new(ImportFields.Name, "Name"),
            new(ImportFields.ShortName, "Short name"), new(ImportFields.VatRate, "VAT rate"), new(ImportFields.Active, "Active"),
            new(ImportFields.OptionGroups, "Option groups"), new(ImportFields.ErpItemCode, "ERPNext item code"),
        };
        fields.AddRange(lists.Select(l => new ImportFieldChoice(ImportFields.PricePrefix + l.Id, $"Price: {l.Name}")));

        var headers = rows[0].Select(h => h.Trim()).ToList();
        var mapping = options.Mapping is { Count: > 0 } given && given.Count == headers.Count
            ? given.ToList()
            : headers.Select(h => AutoMap(h, lists, defaultList)).ToList();
        string? problem = null;
        if (!mapping.Contains(ImportFields.Name)) problem = "Choose the column with the item name.";
        else if (!mapping.Contains(ImportFields.Category)) problem = "Choose the column with the category.";
        else if (!mapping.Contains(ImportFields.PricePrefix + defaultList.Id)) problem = $"Choose the column with the {defaultList.Name} price.";
        else if (mapping.Where(m => m != ImportFields.Ignore).GroupBy(m => m).Any(g => g.Count() > 1)) problem = "Each field can come from one column only.";

        var previewRows = new List<ImportRow>();
        var work = new List<WorkRow>();
        var newCategories = new List<string>();
        var seen = new HashSet<string>();
        if (problem is null)
        {
            for (var r = 1; r < rows.Count; r++)
            {
                var cells = rows[r];
                string Cell(string field)
                {
                    var index = mapping.IndexOf(field);
                    return index >= 0 && index < cells.Count ? cells[index].Trim() : "";
                }
                bool Has(string field) => mapping.Contains(field);

                var name = Cell(ImportFields.Name);
                var categoryName = Cell(ImportFields.Category);
                var line = r + 1;
                string? error = null;

                if (name.Length is < MenuItem.MinNameLength or > MenuItem.MaxNameLength) error = $"The name must be {MenuItem.MinNameLength} to {MenuItem.MaxNameLength} characters.";
                else if (categoryName.Length is < 2 or > 60) error = "The category is missing.";
                else if (!seen.Add(name.ToUpperInvariant())) error = "This name is in the file twice.";

                items.TryGetValue(name.ToUpperInvariant(), out var existing);
                var categoryKey = categoryName.ToUpperInvariant();
                var w = new WorkRow { Name = existing?.Name ?? name, CategoryKey = categoryKey, Existing = existing };

                if (error is null && Has(ImportFields.ShortName) && Cell(ImportFields.ShortName) is { Length: > 0 } sn)
                {
                    if (sn.Length > MenuItem.MaxShortNameLength) error = $"The short name can be at most {MenuItem.MaxShortNameLength} characters.";
                    else w.ShortName = sn;
                }
                if (error is null && Has(ImportFields.VatRate))
                {
                    w.HasVat = true;
                    if (!MenuCsv.TryMoney(Cell(ImportFields.VatRate), out var vat) || vat is < 0m or >= 100m) error = "The VAT rate is not a number from 0 to 99.";
                    else w.Vat = vat;
                }
                if (error is null && Has(ImportFields.Active))
                {
                    if (!MenuCsv.TryBool(Cell(ImportFields.Active), out var active)) error = "Active must be yes or no.";
                    else w.Active = active;
                }
                if (error is null && Has(ImportFields.ErpItemCode)) w.ErpCode = Cell(ImportFields.ErpItemCode);
                if (error is null && Has(ImportFields.OptionGroups))
                {
                    var names = Cell(ImportFields.OptionGroups).Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var unknown = names.FirstOrDefault(n => !groups.ContainsKey(n.ToUpperInvariant()));
                    if (unknown is not null) error = $"Unknown option group \"{unknown}\".";
                    else w.GroupIds = names.Select(n => groups[n.ToUpperInvariant()].Id).Distinct().ToList();
                }
                if (error is null)
                {
                    foreach (var list in lists.Where(l => Has(ImportFields.PricePrefix + l.Id)))
                    {
                        var text = Cell(ImportFields.PricePrefix + list.Id);
                        if (!MenuCsv.TryMoney(text, out var price)) { error = $"The {list.Name} price \"{text}\" is not a number."; break; }
                        if (price is null) continue;
                        if (price < 0m) { error = $"The {list.Name} price can't be negative."; break; }
                        if (price == 0m) { error = $"The {list.Name} price is 0. Set free items in the admin screen."; break; }
                        w.Prices[list.Id] = decimal.Round(price.Value, 2, MidpointRounding.AwayFromZero);
                    }
                }
                if (error is null && existing is null && !w.Prices.ContainsKey(defaultList.Id))
                    error = $"A new item needs a {defaultList.Name} price.";

                if (error is not null)
                {
                    previewRows.Add(new ImportRow(line, ImportStatus.Error, name, categoryName, [], error));
                    continue;
                }

                if (!categories.ContainsKey(categoryKey) && !newCategories.Any(c => c.ToUpperInvariant() == categoryKey))
                    newCategories.Add(categoryName);

                if (existing is null)
                {
                    var changes = w.Prices.Select(p => new ImportChange(Label(lists, p.Key), null, MenuCsv.Money(p.Value), true)).ToList();
                    previewRows.Add(new ImportRow(line, ImportStatus.New, name, categoryName, changes, null));
                    work.Add(w);
                    continue;
                }
                if (!options.UpdateExisting)
                {
                    previewRows.Add(new ImportRow(line, ImportStatus.Unchanged, existing.Name, categoryName, [], null));
                    continue;
                }

                var diff = new List<ImportChange>();
                var oldCategory = categoryNames.GetValueOrDefault(existing.CategoryId, "");
                if (!string.Equals(oldCategory, categoryName, StringComparison.OrdinalIgnoreCase))
                    diff.Add(new ImportChange("Category", oldCategory, categoryName, false));
                if (w.ShortName is { } s && s != existing.ShortName) diff.Add(new ImportChange("Short name", existing.ShortName, s, false));
                if (w.HasVat && w.Vat != existing.VatRate)
                    diff.Add(new ImportChange("VAT rate", Vat(existing.VatRate), Vat(w.Vat), false));
                if (w.Active is { } a && a != existing.Active) diff.Add(new ImportChange("Active", Yes(existing.Active), Yes(a), false));
                if (w.ErpCode is { } e && e != (existing.ErpItemCode ?? "")) diff.Add(new ImportChange("ERPNext item code", existing.ErpItemCode, e, false));
                if (w.GroupIds is { } gids)
                {
                    var current = assignments.Where(x => x.MenuItemId == existing.Id).Select(x => x.GroupId).ToHashSet();
                    if (!current.SetEquals(gids))
                        diff.Add(new ImportChange("Option groups",
                            string.Join("; ", current.Select(g => groupNames[g]).Order()),
                            string.Join("; ", gids.Select(g => groupNames[g]).Order()), false));
                }
                foreach (var (listId, price) in w.Prices)
                {
                    var old = MenuRules.PriceOn(existing, listId);
                    if (old != price) diff.Add(new ImportChange(Label(lists, listId), old is { } o ? MenuCsv.Money(o) : null, MenuCsv.Money(price), true));
                }

                previewRows.Add(new ImportRow(line, diff.Count > 0 ? ImportStatus.Updated : ImportStatus.Unchanged, existing.Name, categoryName, diff, null));
                if (diff.Count > 0) work.Add(w);
            }
        }

        var toDeactivate = options.DeactivateMissing && problem is null
            ? items.Values.Where(i => i.Active && !seen.Contains(i.Name.ToUpperInvariant())).OrderBy(i => i.Name).ToList()
            : [];
        var preview = new ImportPreview(
            headers, mapping, fields, problem is not null, problem, previewRows, newCategories,
            toDeactivate.Select(i => i.Name).ToList(),
            previewRows.Count(r => r.Status == ImportStatus.New),
            previewRows.Count(r => r.Status == ImportStatus.Updated),
            previewRows.Count(r => r.Status == ImportStatus.Unchanged),
            previewRows.Count(r => r.Status == ImportStatus.Error),
            previewRows.Where(r => r.Status is ImportStatus.New or ImportStatus.Updated).Sum(r => r.Changes.Count(c => c.IsPrice)));

        var lastSort = (await db.MenuItems.AsNoTracking().GroupBy(i => i.CategoryId).Select(g => new { g.Key, Max = g.Max(i => i.SortOrder) }).ToListAsync(ct))
            .Where(x => categoryNames.ContainsKey(x.Key))
            .ToDictionary(x => categoryNames[x.Key].ToUpperInvariant(), x => x.Max);
        return new PlanResult(preview, work, categories, lastSort, assignments, toDeactivate);
    }

    private static string Label(List<PriceList> lists, Guid id) => $"Price: {lists.First(l => l.Id == id).Name}";
    private static string? Vat(decimal? v) => v is { } x ? MenuCsv.Money(x) + "%" : "Business default";
    private static string Yes(bool b) => b ? "yes" : "no";

    /// <summary>Guesses a column's field from its header. "price" is the default list; "price2" is "Price 2".</summary>
    public static string AutoMap(string header, IReadOnlyList<PriceList> lists, PriceList defaultList)
    {
        var h = new string(header.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var list = lists.FirstOrDefault(l => new string(l.Name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()) == h);
        if (list is not null) return ImportFields.PricePrefix + list.Id;
        return h switch
        {
            "category" or "categoryname" or "group" or "groupcode" => ImportFields.Category,
            "name" or "item" or "itemname" or "product" or "productname" => ImportFields.Name,
            "shortname" => ImportFields.ShortName,
            "vat" or "vatrate" or "tax" or "taxrate" => ImportFields.VatRate,
            "active" or "status" => ImportFields.Active,
            "optiongroups" or "options" or "modifiers" => ImportFields.OptionGroups,
            "erpnextitemcode" or "erpitemcode" or "itemcode" => ImportFields.ErpItemCode,
            "price" or "price1" => ImportFields.PricePrefix + defaultList.Id,
            _ => ImportFields.Ignore,
        };
    }
}
