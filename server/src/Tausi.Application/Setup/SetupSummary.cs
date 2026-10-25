using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Settings;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Setup;
using Tausi.Domain.Stock;

namespace Tausi.Application.Setup;

public sealed record SummaryLine(string Step, string Title, string Text, bool Skipped, string? LaterLink);

public sealed record SetupSummaryDto(IReadOnlyList<SummaryLine> Lines);

/// <summary>The Finish page: what was set up, and what was skipped with where to do it later.</summary>
public sealed class SetupSummary(ITausiDb db, LicenceService licences)
{
    public async Task<SetupSummaryDto> Build(CancellationToken ct)
    {
        var progress = await db.SetupProgress.AsNoTracking().ToDictionaryAsync(p => p.Step, ct);
        var settings = await SettingsRegistry.Load(db, ct);
        var adminRole = await db.Roles.Where(r => r.SystemKey == RoleKeys.Admin).Select(r => r.Id).FirstOrDefaultAsync(ct);
        var admins = await db.Users.CountAsync(u => u.Active && u.RoleId == adminRole, ct);
        var staff = await db.Users.CountAsync(u => u.Active && u.RoleId != adminRole, ct);
        var printers = await db.Printers.CountAsync(ct);
        var terminals = await db.Terminals.CountAsync(t => t.RevokedAt == null, ct);
        var paired = await db.Terminals.CountAsync(t => t.DeviceTokenHash != null && t.RevokedAt == null, ct);
        var items = await db.MenuItems.CountAsync(ct);
        var categories = await db.Categories.CountAsync(ct);
        var lists = await db.PriceLists.CountAsync(ct);
        var groups = await db.ModifierGroups.CountAsync(ct);
        var tables = await db.Tables.CountAsync(ct);
        var stockItems = await db.StockItems.CountAsync(ct);
        var counts = await db.StockCounts.CountAsync(c => c.IsOpening && c.Status == StockCountStatus.Approved, ct);
        var opening = await db.MoneyDocuments.AnyAsync(d => d.Kind == MoneyDocumentKind.OpeningBalances && d.Status == MoneyDocumentStatus.Posted, ct);
        var licence = await licences.Status(ct);

        SummaryLine Line(string step, string text, string? later)
        {
            var skipped = progress.TryGetValue(step, out var p) ? p.Status == SetupStepStatus.Skipped : true;
            return new SummaryLine(step, SetupFlow.Title(step), skipped ? $"Skipped. {text}" : text, skipped, skipped ? later : null);
        }
        return new SetupSummaryDto(
        [
            Line(SetupSteps.Business, $"{settings.String(SettingsRegistry.BusinessName)} · VAT {settings.Decimal(SettingsRegistry.BusinessVatRate):0.##}% · " +
                $"{settings.String(SettingsRegistry.BusinessCurrency)} · {settings.String(SettingsRegistry.BusinessTimeZoneKey)}", null),
            Line(SetupSteps.Admin, $"{Count(admins, "admin")} and {Count(staff, "other person", "other people")}", "/admin/staff"),
            Line(SetupSteps.Devices, $"{Count(printers, "printer")}, {Count(terminals, "terminal")} ({paired} paired)", "/admin/terminals"),
            Line(SetupSteps.Menu, $"{Count(items, "item")} in {Count(categories, "category", "categories")}, {Count(lists, "price list")}, " +
                $"{Count(groups, "modifier group")}, {Count(tables, "table")}", "/admin/menu"),
            Line(SetupSteps.Stock, $"{Count(stockItems, "stock item")}, {Count(counts, "opening count")}" +
                (opening ? ", opening balances entered" : ", no opening balances"), "/admin/stock"),
            Line(SetupSteps.Licence, licence.IsTrial
                ? $"30-day trial: {licence.DaysLeft} days left, {licence.TerminalLimit} terminals"
                : licence.Status == "None" ? "No licence yet" : $"{licence.Edition} licence for {licence.Business}, {licence.TerminalLimit} terminals" +
                    (licence.ExpiresAt is { } e ? $", until {e:d MMM yyyy}" : ", no end date"), null),
        ]);
    }

    private static string Count(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";
}
