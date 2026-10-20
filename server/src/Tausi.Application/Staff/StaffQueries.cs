using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Domain.People;

namespace Tausi.Application.Staff;

/// <summary>Builds staff rows, including last login and terminals used in the last 7 days (from the audit log).</summary>
public static class StaffQueries
{
    public static readonly TimeSpan UsageWindow = TimeSpan.FromDays(7);

    public static async Task<IReadOnlyList<StaffMemberDto>> Rows(
        ITausiDb db, IReadOnlyCollection<User> users, DateTimeOffset now, CancellationToken ct)
    {
        var ids = users.Select(u => u.Id).ToList();
        var since = now - UsageWindow;
        var terminalNames = await db.Terminals.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var used = await db.AuditEntries.AsNoTracking()
            .Where(a => a.Action == AuditActions.PinLogin && a.At >= since && a.UserId != null && ids.Contains(a.UserId.Value) && a.TerminalId != null)
            .Select(a => new { UserId = a.UserId!.Value, TerminalId = a.TerminalId!.Value })
            .Distinct()
            .ToListAsync(ct);

        var rows = new List<StaffMemberDto>();
        foreach (var u in users)
        {
            var last = await db.AuditEntries.AsNoTracking()
                .Where(a => a.UserId == u.Id && (a.Action == AuditActions.PinLogin || a.Action == AuditActions.AdminLogin))
                .OrderByDescending(a => a.At)
                .Select(a => new { a.At, a.TerminalId, a.Action })
                .FirstOrDefaultAsync(ct);
            var terminals = used.Where(x => x.UserId == u.Id)
                .Select(x => terminalNames.GetValueOrDefault(x.TerminalId, "?"))
                .Distinct()
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            rows.Add(new StaffMemberDto(
                u.Id, u.Name, u.RoleId, u.Role?.Name ?? "", u.Active, u.MustChangePin, u.UserName,
                u.PasswordHash is not null,
                last is null ? null : new LastLoginDto(last.At,
                    last.TerminalId is { } t ? terminalNames.GetValueOrDefault(t) : (last.Action == AuditActions.AdminLogin ? "Admin site" : null)),
                terminals,
                u.RowVersion));
        }
        return rows;
    }
}
