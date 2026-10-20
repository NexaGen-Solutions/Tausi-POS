using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Tables;

public sealed record AdminAreaDto(Guid Id, string Name, int SortOrder, bool Active, int Tables, long RowVersion);

public sealed record AdminTableDto(
    Guid Id, string Name, Guid AreaId, string AreaName, int SortOrder, bool Active,
    IReadOnlyList<long> OpenTickets, bool HasHistory, long RowVersion);

public sealed record TablesAdminDto(IReadOnlyList<AdminAreaDto> Areas, IReadOnlyList<AdminTableDto> Tables);

public sealed record AreaRequest(string Name, int SortOrder, bool Active, long RowVersion = 0);

public sealed record TableRequest(string Name, Guid AreaId, int SortOrder, bool Active, long RowVersion = 0);

/// <param name="Prefix">Text before each number, e.g. "Pool" for "Pool 1", "Pool 2".</param>
public sealed record QuickAddRequest(int From, int To, Guid AreaId, string? Prefix);

public sealed record QuickAddResult(IReadOnlyList<string> Added, IReadOnlyList<string> Skipped);

/// <summary>Shared by the Tables admin use cases: <c>settings.manage</c>, names, and the "in use" rule.</summary>
public sealed class TableAdmin(ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval)
{
    public const int MaxQuickAdd = 200;

    public Task<PermissionGrant> Require(CancellationToken ct) =>
        permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);

    public static string CleanName(string? name, int max, string what)
    {
        var clean = string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length is 0 || clean.Length > max) throw new DomainException($"The {what} name must be 1 to {max} characters.");
        return clean;
    }

    /// <summary>Open tickets on these tables, by table.</summary>
    public async Task<Dictionary<Guid, List<long>>> OpenTickets(IReadOnlyCollection<Guid> tableIds, CancellationToken ct) =>
        (await db.Tickets.AsNoTracking()
            .Where(k => k.TableId != null && tableIds.Contains(k.TableId.Value) && k.Status == TicketStatus.Open)
            .Select(k => new { TableId = k.TableId!.Value, k.Number }).ToListAsync(ct))
        .GroupBy(k => k.TableId).ToDictionary(g => g.Key, g => g.Select(k => k.Number).Order().ToList());

    /// <summary>"Table 7 has open tickets: #1042, #1047. Settle, move or merge them first."</summary>
    public async Task EnsureNoOpenTickets(IReadOnlyCollection<DiningTable> tables, CancellationToken ct)
    {
        var open = await OpenTickets(tables.Select(t => t.Id).ToList(), ct);
        var busy = tables.Where(t => open.ContainsKey(t.Id)).ToList();
        if (busy.Count == 0) return;
        var parts = busy.Select(t => $"{t.Name} ({string.Join(", ", open[t.Id].Select(n => $"#{n}"))})");
        throw new DomainException(
            $"These tables have open tickets: {string.Join("; ", parts)}. Settle, move or merge them first.", TicketCodes.TableInUse);
    }
}

public sealed class GetTablesAdmin(ITausiDb db, TableAdmin admin)
{
    public async Task<TablesAdminDto> Execute(CancellationToken ct)
    {
        await admin.Require(ct);
        var areas = await db.Areas.AsNoTracking().ToListAsync(ct);
        var tables = await db.Tables.AsNoTracking().ToListAsync(ct);
        var open = await admin.OpenTickets(tables.Select(t => t.Id).ToList(), ct);
        var withHistory = (await db.Tickets.AsNoTracking().Where(k => k.TableId != null)
            .Select(k => k.TableId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var names = areas.ToDictionary(a => a.Id, a => a.Name);

        return new TablesAdminDto(
            areas.OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
                .Select(a => new AdminAreaDto(a.Id, a.Name, a.SortOrder, a.Active, tables.Count(t => t.AreaId == a.Id), a.RowVersion)).ToList(),
            tables.OrderBy(t => areas.First(a => a.Id == t.AreaId).SortOrder).ThenBy(t => names[t.AreaId])
                .ThenBy(t => t.SortOrder).ThenBy(t => t.Name, NaturalOrder.Instance)
                .Select(t => new AdminTableDto(t.Id, t.Name, t.AreaId, names[t.AreaId], t.SortOrder, t.Active,
                    open.GetValueOrDefault(t.Id) ?? [], withHistory.Contains(t.Id), t.RowVersion)).ToList());
    }
}

public sealed class SaveArea(ITausiDb db, TableAdmin admin, IAuditLog audit, TicketEvents events)
{
    public async Task<AdminAreaDto> Execute(Guid? id, AreaRequest request, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        var name = TableAdmin.CleanName(request.Name, Area.MaxNameLength, "area");
        var area = id is null ? new Area() : await db.Areas.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Area not found.");
        if (id is not null && area.RowVersion != request.RowVersion)
            throw new ConflictException("This area was changed by someone else. Reload and try again.", "stale");
        var others = await db.Areas.AsNoTracking().Where(a => a.Id != area.Id).Select(a => a.Name).ToListAsync(ct);
        if (others.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException($"There is already an area called {name}.");
        if (area.Active && !request.Active)
            await admin.EnsureNoOpenTickets(await db.Tables.Where(t => t.AreaId == area.Id).ToListAsync(ct), ct);

        var old = id is null ? null : new { area.Name, area.SortOrder, area.Active };
        area.Name = name;
        area.SortOrder = request.SortOrder;
        area.Active = request.Active;
        if (id is null) db.Areas.Add(area);
        audit.Write(new AuditRecord(AuditActions.AreaChanged, nameof(Area), area.Id.ToString(), old,
            new { area.Name, area.SortOrder, area.Active }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await events.TablesChanged(ct);
        return new AdminAreaDto(area.Id, area.Name, area.SortOrder, area.Active,
            await db.Tables.CountAsync(t => t.AreaId == area.Id, ct), area.RowVersion);
    }
}

public sealed class SaveTable(ITausiDb db, TableAdmin admin, IAuditLog audit, TicketEvents events)
{
    public async Task Execute(Guid? id, TableRequest request, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        var name = TableAdmin.CleanName(request.Name, DiningTable.MaxNameLength, "table");
        if (!await db.Areas.AnyAsync(a => a.Id == request.AreaId, ct)) throw new DomainException("Choose an area.");
        var table = id is null ? new DiningTable() : await db.Tables.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Table not found.");
        if (id is not null && table.RowVersion != request.RowVersion)
            throw new ConflictException("This table was changed by someone else. Reload and try again.", "stale");
        var others = await db.Tables.AsNoTracking().Where(t => t.Id != table.Id).Select(t => t.Name).ToListAsync(ct);
        if (others.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException($"There is already a table called {name}.");
        if (table.Active && !request.Active && id is not null) await admin.EnsureNoOpenTickets([table], ct);

        var old = id is null ? null : new { table.Name, table.AreaId, table.SortOrder, table.Active };
        table.Name = name;
        table.AreaId = request.AreaId;
        table.SortOrder = request.SortOrder;
        table.Active = request.Active;
        if (id is null) db.Tables.Add(table);
        audit.Write(new AuditRecord(AuditActions.TableChanged, "Table", table.Id.ToString(), old,
            new { table.Name, table.AreaId, table.SortOrder, table.Active }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await events.TablesChanged(ct);
    }
}

/// <summary>"Add tables from 1 to 16 in Main": creates them in one go, skipping names that exist.</summary>
public sealed class QuickAddTables(ITausiDb db, TableAdmin admin, IAuditLog audit, TicketEvents events)
{
    public async Task<QuickAddResult> Execute(QuickAddRequest request, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        if (request.From < 0 || request.To < request.From)
            throw new DomainException("Choose numbers from low to high, e.g. from 1 to 16.");
        if (request.To - request.From + 1 > TableAdmin.MaxQuickAdd)
            throw new DomainException($"Add at most {TableAdmin.MaxQuickAdd} tables at a time.");
        if (!await db.Areas.AnyAsync(a => a.Id == request.AreaId, ct)) throw new DomainException("Choose an area.");
        var prefix = string.IsNullOrWhiteSpace(request.Prefix) ? "" : TableAdmin.CleanName(request.Prefix, DiningTable.MaxNameLength - 4, "table") + " ";

        var existing = (await db.Tables.AsNoTracking().Select(t => t.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextSort = (await db.Tables.Where(t => t.AreaId == request.AreaId).MaxAsync(t => (int?)t.SortOrder, ct) ?? 0) + 1;
        var added = new List<string>();
        var skipped = new List<string>();
        for (var n = request.From; n <= request.To; n++)
        {
            var name = prefix + n;
            if (!existing.Add(name)) { skipped.Add(name); continue; }
            db.Tables.Add(new DiningTable { Name = name, AreaId = request.AreaId, SortOrder = nextSort++ });
            added.Add(name);
        }
        if (added.Count > 0)
        {
            audit.Write(new AuditRecord(AuditActions.TablesAdded, nameof(Area), request.AreaId.ToString(),
                NewValue: new { added, skipped }, ApprovedByUserId: grant.ApprovedByUserId));
            await db.SaveChangesAsync(ct);
            await events.TablesChanged(ct);
        }
        return new QuickAddResult(added, skipped);
    }
}

/// <summary>
/// Deletes a table or an area that was never used. A table with ticket history can only be
/// switched off, so its tickets keep their table; an area must be empty.
/// </summary>
public sealed class DeleteTableOrArea(ITausiDb db, TableAdmin admin, IAuditLog audit, TicketEvents events)
{
    public async Task Table(Guid id, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        var table = await db.Tables.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Table not found.");
        await admin.EnsureNoOpenTickets([table], ct);
        if (await db.Tickets.AnyAsync(k => k.TableId == id, ct))
            throw new DomainException($"{table.Name} has tickets in its history, so it can only be switched off.");
        db.Tables.Remove(table);
        audit.Write(new AuditRecord(AuditActions.TableChanged, "Table", id.ToString(), new { table.Name, table.AreaId }, null, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await events.TablesChanged(ct);
    }

    public async Task Area(Guid id, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        var area = await db.Areas.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Area not found.");
        if (await db.Tables.AnyAsync(t => t.AreaId == id, ct))
            throw new DomainException($"{area.Name} still has tables. Move or delete them first, or switch the area off.");
        db.Areas.Remove(area);
        audit.Write(new AuditRecord(AuditActions.AreaChanged, nameof(Domain.Service.Area), id.ToString(), new { area.Name }, null, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await events.TablesChanged(ct);
    }
}
