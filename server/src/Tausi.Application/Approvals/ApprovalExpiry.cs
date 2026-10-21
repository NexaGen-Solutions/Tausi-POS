using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Events;
using Tausi.Application.Settings;
using Tausi.Domain.Service;

namespace Tausi.Application.Approvals;

/// <summary>A request that has just been decided, for <c>approval.resolved</c>.</summary>
public sealed record ResolvedApproval(
    Guid RequestId, ApprovalKind Kind, Guid TicketId, ApprovalStatus Status, Guid RequestedByUserId, string? DecidedBy, string? Note);

/// <summary>
/// Pending requests expire after <c>pos.approvalExpiryMinutes</c> (default 30), and when their
/// ticket is merged away or no longer open, so nobody approves something that no longer fits.
/// </summary>
public static class ApprovalExpiry
{
    public const string TimedOut = "No one decided in time.";
    public const string TicketMerged = "The ticket was merged into another.";
    public const string TicketClosed = "The ticket is no longer open.";

    /// <summary>Expires the pending requests on these tickets, in the caller's unit of work.</summary>
    public static async Task<List<ResolvedApproval>> ForTickets(
        ITausiDb db, IReadOnlyCollection<Guid> ticketIds, string note, DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.ApprovalRequests
            .Where(r => ticketIds.Contains(r.TicketId) && r.Status == ApprovalStatus.Pending).ToListAsync(ct);
        return pending.Select(r => Expire(r, note, now)).ToList();
    }

    public static ResolvedApproval Expire(ApprovalRequest request, string note, DateTimeOffset now)
    {
        request.Status = ApprovalStatus.Expired;
        request.DecidedAt = now;
        request.DecisionNote = note;
        return Resolved(request, null);
    }

    public static ResolvedApproval Resolved(ApprovalRequest r, string? decidedBy) =>
        new(r.Id, r.Kind, r.TicketId, r.Status, r.RequestedByUserId, decidedBy, r.DecisionNote);

    public static async Task Publish(IPosEvents events, IEnumerable<ResolvedApproval> resolved, CancellationToken ct)
    {
        foreach (var r in resolved)
            await events.Publish(PosEventNames.ApprovalResolved, new
            {
                requestId = r.RequestId,
                kind = r.Kind.ToString(),
                ticketId = r.TicketId,
                status = r.Status.ToString(),
                requestedByUserId = r.RequestedByUserId,
                decidedBy = r.DecidedBy,
                note = r.Note,
            }, ct);
    }
}

/// <summary>The sweep the background service runs every few seconds (and the lists run first).</summary>
public sealed class ExpireApprovals(ITausiDb db, IPosEvents events, IAuditLog audit, TimeProvider clock)
{
    public async Task<int> Run(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var settings = await PosSettings.Load(db, ct);
        var cutoff = now - settings.ApprovalExpiry;
        var stale = await db.ApprovalRequests
            .Where(r => r.Status == ApprovalStatus.Pending)
            .Where(r => r.RequestedAt <= cutoff || !db.Tickets.Any(k => k.Id == r.TicketId && k.Status == TicketStatus.Open))
            .ToListAsync(ct);
        if (stale.Count == 0) return 0;

        var resolved = new List<ResolvedApproval>();
        foreach (var request in stale)
        {
            var note = request.RequestedAt <= cutoff ? ApprovalExpiry.TimedOut : ApprovalExpiry.TicketClosed;
            resolved.Add(ApprovalExpiry.Expire(request, note, now));
            audit.Write(new AuditRecord(AuditActions.ApprovalExpired, nameof(ApprovalRequest), request.Id.ToString(),
                NewValue: new { request.Summary, note }, UserId: request.RequestedByUserId, TerminalId: request.TerminalId));
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone decided one of them meanwhile; the next sweep looks again.
            return 0;
        }
        await ApprovalExpiry.Publish(events, resolved, ct);
        return resolved.Count;
    }
}
