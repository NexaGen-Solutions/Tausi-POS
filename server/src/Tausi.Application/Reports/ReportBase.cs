using Microsoft.EntityFrameworkCore;
using Tausi.Domain.Service;

namespace Tausi.Application.Reports;

/// <summary>What every report shares: its result header (period, the filters in words) and the open-tickets note.</summary>
public abstract class ReportBase : IReport
{
    public abstract ReportDefinition Definition { get; }

    public abstract Task<ReportResult> Run(ReportContext context, CancellationToken ct);

    protected async Task<ReportResult> Result(ReportContext c, IReadOnlyList<ReportTable> tables, CancellationToken ct, params string?[] notes) =>
        new(Definition.Key, Definition.Title, Definition.UsesPeriod ? c.Period.Label : "", await FilterText(c, ct), c.Now, tables,
            notes.OfType<string>().ToList());

    /// <summary>"Includes 2 open tickets" when open tickets are counted and there are some.</summary>
    protected static async Task<string?> OpenNote(ReportContext c, CancellationToken ct)
    {
        if (!c.IncludeOpen) return null;
        var ids = c.Period.PeriodIds;
        var open = await c.Db.Tickets.AsNoTracking().CountAsync(k => ids.Contains(k.WorkPeriodId) && k.Status == TicketStatus.Open, ct);
        return open == 0 ? null : open == 1 ? "Includes 1 open ticket" : $"Includes {open} open tickets";
    }

    /// <summary>The filters in use, in words, for the header of the screen, PDF, Excel and slip.</summary>
    private async Task<List<string>> FilterText(ReportContext c, CancellationToken ct)
    {
        var f = c.Filters;
        var uses = Definition.Filters;
        var text = new List<string>();
        void Add(string filter, bool set, Func<string> words)
        {
            if (uses.Contains(filter) && set) text.Add(words());
        }

        Add(ReportFilter.User, f.UserId is not null, () => $"User: {c.UserName(f.UserId)}");
        Add(ReportFilter.Cashier, f.CashierId is not null, () => $"Cashier: {c.UserName(f.CashierId)}");
        Add(ReportFilter.Waiter, f.WaiterId is not null, () => $"Waiter: {c.UserName(f.WaiterId)}");
        Add(ReportFilter.Requester, f.RequesterId is not null, () => $"Asked by: {c.UserName(f.RequesterId)}");
        Add(ReportFilter.Approver, f.ApproverId is not null, () => $"Approved by: {c.UserName(f.ApproverId)}");
        Add(ReportFilter.Terminal, f.TerminalId is not null, () => $"Terminal: {c.TerminalName(f.TerminalId)}");
        Add(ReportFilter.PaymentType, f.PaymentType is not null, () => $"Payment: {(f.PaymentType == PaymentType.Mpesa ? "M-Pesa" : f.PaymentType.ToString())}");
        Add(ReportFilter.Kind, !string.IsNullOrEmpty(f.Kind), () => $"Kind: {f.Kind}");
        Add(ReportFilter.Status, f.Status is not null, () => $"Status: {f.Status}");
        Add(ReportFilter.Amount, f.MinAmount is not null || f.MaxAmount is not null,
            () => $"Amount: {(f.MinAmount is { } min ? ReportFormat.Money(min) : "0")} to {(f.MaxAmount is { } max ? ReportFormat.Money(max) : "any")}");
        Add(ReportFilter.Flags, f.HasVoids || f.HasReprints || f.HasDiscount,
            () => "Only tickets with " + string.Join(", ", new[] { f.HasVoids ? "voids" : null, f.HasReprints ? "reprints" : null, f.HasDiscount ? "a discount" : null }.OfType<string>()));
        Add(ReportFilter.StockCategory, f.StockCategory is not null, () => $"Category: {f.StockCategory}");
        Add(ReportFilter.OnlyBelowReorder, f.OnlyBelowReorder, () => "Only below the reorder level");
        Add(ReportFilter.Action, !string.IsNullOrEmpty(f.Action), () => $"Action: {f.Action}");
        Add(ReportFilter.Entity, !string.IsNullOrEmpty(f.Entity), () => $"Entity: {f.Entity}");
        Add(ReportFilter.Search, !string.IsNullOrEmpty(f.Search), () => $"Search: \"{f.Search}\"");
        if (uses.Contains(ReportFilter.CreditTo))
            text.Add(f.CreditTo == CreditTo.OpenedBy ? "Sales credited to who opened the ticket" : "Sales credited to who added the item");
        if (uses.Contains(ReportFilter.IncludeOpen) && c.Period.HasOpenPeriod)
            text.Add(c.IncludeOpen ? "Open tickets included" : "Open tickets left out");

        if (uses.Contains(ReportFilter.Location) && f.LocationId is { } location)
            text.Add($"Location: {await c.Db.StockLocations.AsNoTracking().Where(l => l.Id == location).Select(l => l.Name).FirstOrDefaultAsync(ct)}");
        if (uses.Contains(ReportFilter.Category) && f.CategoryId is { } category)
            text.Add($"Category: {await c.Db.Categories.AsNoTracking().Where(x => x.Id == category).Select(x => x.Name).FirstOrDefaultAsync(ct)}");
        if (uses.Contains(ReportFilter.Supplier) && f.SupplierId is { } supplier)
            text.Add($"Supplier: {await c.Db.Suppliers.AsNoTracking().Where(x => x.Id == supplier).Select(x => x.Name).FirstOrDefaultAsync(ct)}");
        if (uses.Contains(ReportFilter.Table) && f.TableId is { } table)
            text.Add($"Table: {await c.Db.Tables.AsNoTracking().Where(x => x.Id == table).Select(x => x.Name).FirstOrDefaultAsync(ct)}");
        if (uses.Contains(ReportFilter.Role) && f.RoleId is { } role)
            text.Add($"Role: {await c.Db.Roles.AsNoTracking().Where(x => x.Id == role).Select(x => x.Name).FirstOrDefaultAsync(ct)}");
        if (uses.Contains(ReportFilter.AsAt))
            text.Add($"As at the end of {(f.AsAt ?? Money.BusinessDates.Of(c.Now, c.Zone)):ddd d MMM yyyy}");
        return text;
    }
}
