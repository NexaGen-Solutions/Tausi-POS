using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.Devices;
using Tausi.Domain.People;

namespace Tausi.Application.Terminals;

public static class TerminalStatus
{
    public const string Online = "Online";
    public const string Offline = "Offline";
    public const string Revoked = "Revoked";
    public const string NotPaired = "NotPaired";
}

public sealed record TerminalRow(
    Guid Id,
    string Name,
    string Status,
    DateTimeOffset? LastSeenAt,
    string? CurrentUser,
    string? AppVersion,
    Guid? PriceListId,
    string? PriceListName,
    Guid? DispensePrinterId,
    string? DispensePrinterName,
    string? DispenseHeader,
    string EffectiveDispenseHeader,
    Guid? ReceiptPrinterId,
    string? ReceiptPrinterName,
    bool Active,
    bool Paired,
    IReadOnlyList<string> LocalPrinters,
    string RoutingPreview,
    long RowVersion,
    Guid? SellsFromLocationId = null,
    string? SellsFromLocationName = null);

public sealed record TerminalRequest(
    string Name,
    Guid? PriceListId,
    Guid? DispensePrinterId,
    string? DispenseHeader,
    Guid? ReceiptPrinterId,
    bool Active,
    long RowVersion,
    Guid? SellsFromLocationId = null);

/// <summary>Shared by the terminal use cases: the permission check and building rows.</summary>
public sealed class TerminalAdmin(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    TerminalPresence presence, TimeProvider clock)
{
    public Task<PermissionGrant> Require(CancellationToken ct) =>
        permissions.Require(session.RequireUser(), PermissionKeys.DevicesManage, approval.ApprovalToken, ct);

    /// <summary>The printer names the terminal reported; blank or unreadable means none reported yet.</summary>
    public static IReadOnlyList<string> LocalPrinters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public async Task<IReadOnlyList<TerminalRow>> Rows(Guid? onlyId, CancellationToken ct)
    {
        var terminals = await db.Terminals.AsNoTracking()
            .Include(t => t.DispensePrinter).Include(t => t.ReceiptPrinter)
            .Where(t => onlyId == null || t.Id == onlyId)
            .OrderBy(t => t.Name).ToListAsync(ct);
        var lists = await db.PriceLists.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var locations = await db.StockLocations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var idle = (await PosSettings.Load(db, ct)).IdleLimit;
        var now = clock.GetUtcNow();
        var ids = terminals.Select(t => t.Id).ToList();
        var sessions = await db.UserSessions.AsNoTracking()
            .Where(s => ids.Contains(s.TerminalId) && s.EndedAt == null)
            .ToListAsync(ct);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        return terminals.Select(t =>
        {
            var current = sessions.Where(s => s.TerminalId == t.Id && s.EndReasonAt(now, idle) is null)
                .OrderByDescending(s => s.LastSeenAt).FirstOrDefault();
            var status = t.RevokedAt is not null ? TerminalStatus.Revoked
                : !t.IsPaired ? TerminalStatus.NotPaired
                : presence.IsOnline(t.Id) ? TerminalStatus.Online
                : TerminalStatus.Offline;
            var lastSeen = new[] { t.LastSeenAt, presence.LastDisconnect(t.Id) }.Max();
            return new TerminalRow(
                t.Id, t.Name, status, lastSeen,
                current is null ? null : users.GetValueOrDefault(current.UserId),
                t.AppVersion,
                t.PriceListId, t.PriceListId is { } p ? lists.GetValueOrDefault(p) : null,
                t.DispensePrinterId, t.DispensePrinter?.Name, t.DispenseHeader, t.EffectiveDispenseHeader,
                t.ReceiptPrinterId, t.ReceiptPrinter?.Name,
                t.Active, t.IsPaired,
                LocalPrinters(t.LocalPrintersJson),
                PrintRouting.Preview(t.Name, t.ReceiptPrinter?.Name, t.EffectiveDispenseHeader, t.DispensePrinter?.Name),
                t.RowVersion, t.SellsFromLocationId, t.SellsFromLocationId is { } l ? locations.GetValueOrDefault(l) : null);
        }).ToList();
    }
}

/// <summary>Terminals with status, current user, price list, printers and routing (devices.manage).</summary>
public sealed class ListTerminals(TerminalAdmin admin)
{
    public async Task<IReadOnlyList<TerminalRow>> Execute(CancellationToken ct)
    {
        await admin.Require(ct);
        return await admin.Rows(null, ct);
    }
}

/// <summary>
/// Edits a terminal: name, price list, dispense printer and header, receipt printer, active, and the
/// stock location it sells from (null keeps it). Any active printer can be chosen for either role.
/// </summary>
public sealed class UpdateTerminal(ITausiDb db, TerminalAdmin admin, IAuditLog audit, IPosEvents events)
{
    public async Task<TerminalRow> Execute(Guid id, TerminalRequest request, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Terminal not found.");
        if (terminal.RowVersion != request.RowVersion)
            throw new ConflictException("This terminal was changed by someone else. Reload and try again.", "stale");
        ((DbContext)db).Entry(terminal).Property(t => t.RowVersion).OriginalValue = request.RowVersion;

        var name = (request.Name ?? "").Trim();
        if (name.Length is < 2 or > 40) throw new DomainException("The terminal name must be 2 to 40 characters.");
        var upper = name.ToUpperInvariant();
        if (await db.Terminals.AnyAsync(t => t.Id != id && t.Name.ToUpper() == upper, ct))
            throw new DomainException($"There is already a terminal called {name}.");
        var header = string.IsNullOrWhiteSpace(request.DispenseHeader) ? null : request.DispenseHeader.Trim();
        if (header?.Length > 40) throw new DomainException("The dispense header can be at most 40 characters.");
        if (request.PriceListId is { } list && !await db.PriceLists.AnyAsync(l => l.Id == list && l.Active, ct))
            throw new DomainException("Choose an active price list.");
        if (request.SellsFromLocationId is { } location && !await db.StockLocations.AnyAsync(l => l.Id == location && l.Active, ct))
            throw new DomainException("Choose an active stock location.");
        foreach (var printerId in new[] { request.DispensePrinterId, request.ReceiptPrinterId }.OfType<Guid>())
            if (!await db.Printers.AnyAsync(p => p.Id == printerId && p.Active, ct))
                throw new DomainException("Choose an active printer.");

        var before = new { terminal.Name, terminal.PriceListId, terminal.DispensePrinterId, terminal.DispenseHeader, terminal.ReceiptPrinterId, terminal.Active, terminal.SellsFromLocationId };
        var priceListChanged = terminal.PriceListId != request.PriceListId;
        terminal.Name = name;
        terminal.PriceListId = request.PriceListId;
        terminal.DispensePrinterId = request.DispensePrinterId;
        terminal.DispenseHeader = header;
        terminal.ReceiptPrinterId = request.ReceiptPrinterId;
        terminal.Active = request.Active;
        if (request.SellsFromLocationId is { } sellsFrom) terminal.SellsFromLocationId = sellsFrom;

        audit.Write(new AuditRecord("terminal.changed", "Terminal", id.ToString(), before,
            new { terminal.Name, terminal.PriceListId, terminal.DispensePrinterId, terminal.DispenseHeader, terminal.ReceiptPrinterId, terminal.Active, terminal.SellsFromLocationId },
            grant.ApprovedByUserId));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("This terminal was changed by someone else. Reload and try again.", "stale");
        }
        await events.Publish(PosEventNames.TerminalStatus, new { terminalId = id }, ct);
        if (priceListChanged) await events.Publish(PosEventNames.MenuChanged, new { }, ct);
        return (await admin.Rows(id, ct))[0];
    }
}

/// <summary>Revokes a terminal's device token: it can't be used any more, and the app returns to pairing.</summary>
public sealed class RevokeTerminal(ITausiDb db, TerminalAdmin admin, SessionKeeper sessions, IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public async Task Execute(Guid id, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException("Terminal not found.");
        await Revoke(db, sessions, terminal, clock.GetUtcNow(), ct);
        audit.Write(new AuditRecord("terminal.revoked", "Terminal", id.ToString(),
            new { paired = true }, new { paired = false }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await events.Publish(PosEventNames.TerminalRevoked, new { terminalId = id }, ct);
        await events.Publish(PosEventNames.TerminalStatus, new { terminalId = id }, ct);
    }

    /// <summary>
    /// Invalidates the token and ends sessions there. The caller saves, then publishes
    /// <c>terminal.revoked</c>: the app checks its token when the event arrives, so it must be gone by then.
    /// </summary>
    public static async Task Revoke(ITausiDb db, SessionKeeper sessions, Terminal terminal, DateTimeOffset now, CancellationToken ct)
    {
        terminal.DeviceTokenHash = null;
        terminal.RevokedAt = now;
        await sessions.EndAllForTerminal(terminal.Id, SessionEndReasons.Revoked, ct);
    }
}
