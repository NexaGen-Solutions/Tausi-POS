using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Ledger;
using Tausi.Domain.Ledger;
using Tausi.Domain.Money;
using Tausi.Domain.Service;
using Tausi.Domain.Stock;

namespace Tausi.Application.Stock;

/// <summary>
/// Stock and its cost follow what the bar pours: printing a line takes its recipe out of the
/// printing terminal's location at the average cost (Cost of sales); voiding it puts the stock back
/// if it wasn't poured, or moves its cost to Stock loss if it was. Lines without a recipe move nothing.
/// </summary>
public sealed class StockSelling(ITausiDb db, StockBook book, ILedgerPostingService ledger)
{
    public const string SaleReference = "OrderLine";
    public const string SaleSource = "StockSale";
    public const string VoidSource = "StockVoid";

    /// <summary>
    /// The print round's sale: one Sale movement per ingredient of each line, and one journal entry
    /// (Cost of sales / Stock) for the round. Call in the print's transaction.
    /// </summary>
    public async Task Sell(
        Ticket ticket, int printRound, IReadOnlyCollection<OrderLine> lines, Guid terminalId, Guid userId, Guid? workPeriodId,
        DateTimeOffset now, CancellationToken ct)
    {
        var location = await db.Terminals.Where(t => t.Id == terminalId).Select(t => t.SellsFromLocationId).FirstOrDefaultAsync(ct);
        if (location is not { } locationId || lines.Count == 0) return;
        var menuIds = lines.Select(l => l.MenuItemId).Distinct().ToList();
        var recipes = await db.RecipeLines.AsNoTracking().Where(r => menuIds.Contains(r.MenuItemId)).ToListAsync(ct);
        if (recipes.Count == 0) return;
        var itemIds = recipes.Select(r => r.StockItemId).Distinct().ToList();
        var costs = await db.StockItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.AverageCost, ct);

        var value = 0m;
        // Stock on hand is updated in one order (by stock item) whatever the ticket's order, so two
        // prints at once on different counters can't lock each other's rows (a database deadlock).
        var moves = lines.SelectMany(line => recipes.Where(r => r.MenuItemId == line.MenuItemId).Select(ingredient => (line, ingredient)))
            .OrderBy(m => m.ingredient.StockItemId).ThenBy(m => m.line.Id).ThenBy(m => m.ingredient.SortOrder);
        foreach (var (line, ingredient) in moves)
        {
            var moved = await book.Move(ingredient.StockItemId, locationId, -ingredient.BaseQuantity * line.Quantity,
                costs[ingredient.StockItemId], StockMovementKind.Sale, SaleReference, line.Id, userId, workPeriodId, ct,
                lineQuantity: line.Quantity, at: now);
            value -= moved.Value;
        }

        var amount = MoneyMath.Round(value);
        if (amount <= 0) return;
        await ledger.Post(new JournalEntryDraft(now, $"Cost of sales · #{ticket.Number} round {printRound}", SaleSource,
            $"{ticket.Id}:{printRound}",
            [JournalLineDraft.Dr(LedgerAccountCodes.CostOfSales, amount), JournalLineDraft.Cr(LedgerAccountCodes.Stock, amount)],
            workPeriodId), ct);
    }

    /// <summary>
    /// A voided part of a printed line. Not poured (and returning stock is on): its share of the sale
    /// goes back at the sale's cost and the cost of sales is reversed. Poured: the stock stays out
    /// and its cost moves from Cost of sales to Stock loss.
    /// </summary>
    public async Task Void(Ticket ticket, OrderLine part, bool poured, Guid userId, Guid? workPeriodId, DateTimeOffset now, CancellationToken ct)
    {
        var sales = await db.StockMovements.AsNoTracking()
            .Where(m => m.Kind == StockMovementKind.Sale && m.ReferenceType == SaleReference && m.ReferenceId == part.SaleLineId)
            .ToListAsync(ct);
        if (sales.Count == 0) return;

        var value = 0m;
        foreach (var sale in sales.Where(s => s.LineQuantity is > 0).OrderBy(s => s.StockItemId).ThenBy(s => s.LocationId))
        {
            var share = -sale.Quantity * part.Quantity / sale.LineQuantity!.Value;
            value += share * sale.UnitCost;
            if (!poured)
                await book.Move(sale.StockItemId, sale.LocationId, share, sale.UnitCost, StockMovementKind.VoidReturn,
                    SaleReference, part.Id, userId, workPeriodId, ct, at: now);
        }

        var amount = MoneyMath.Round(value);
        if (amount <= 0) return;
        var debit = poured ? LedgerAccountCodes.StockLoss : LedgerAccountCodes.Stock;
        await ledger.Post(new JournalEntryDraft(now,
            poured ? $"Voided pour · #{ticket.Number} · {part.Name}" : $"Void returned to stock · #{ticket.Number} · {part.Name}",
            VoidSource, part.Id.ToString(),
            [JournalLineDraft.Dr(debit, amount), JournalLineDraft.Cr(LedgerAccountCodes.CostOfSales, amount)],
            workPeriodId), ct);
    }
}
