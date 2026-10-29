using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Approvals;

public static class ApprovalCodes
{
    public const string NotNeeded = "approval_not_needed";
    public const string OwnRequest = "own_request";
    public const string AlreadyDecided = "already_decided";
    public const string Expired = "request_expired";
    public const string RequestsPending = "requests_pending";
    public const string RequestsOff = "requests_off";
}

/// <summary>
/// "Option 2 · supervisor elsewhere". <see cref="Payload"/> is the action's own request: a
/// <see cref="VoidLineRequest"/>, <see cref="GiftLineRequest"/>, <see cref="DiscountRequest"/>,
/// <see cref="ChangePriceRequest"/>, <see cref="ReprintRequest"/>, <see cref="MoveTicketRequest"/> or
/// <see cref="MergeTicketsRequest"/>.
/// </summary>
public sealed record CreateApprovalRequestDto(
    ApprovalKind Kind, Guid TicketId, Guid? LineId, JsonElement Payload, Guid? PreferredApproverId = null);

public sealed record RejectApprovalRequestDto(string? Note);

/// <summary>Runs (or only checks) the action a request asks for, on tickets the caller has locked.</summary>
public sealed class ApprovalActions(
    ITausiDb db, TicketActions actions, TicketPrints prints, MoveTicket move, MergeTickets merge)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static T Payload<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Json) ?? throw new DomainException("The request is missing its details.");

    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, Json);

    /// <summary>The tickets a request locks: its own, plus the others for a merge.</summary>
    public static List<Guid> TicketIds(ApprovalKind kind, Guid ticketId, string payloadJson) =>
        kind == ApprovalKind.Merge ? MergeTickets.Ids(Payload<MergeTicketsRequest>(payloadJson)) : [ticketId];

    /// <summary>Checks the action still fits and says what it is; changes nothing.</summary>
    public async Task<ActionSummary> Check(ApprovalKind kind, Guid ticketId, Guid? lineId, string payloadJson, Guid terminalId, CancellationToken ct)
    {
        switch (kind)
        {
            case ApprovalKind.Void:
                return TicketActions.CheckVoid(await Ticket(ticketId, ct), Line(lineId), Payload<VoidLineRequest>(payloadJson));
            case ApprovalKind.Gift:
                return TicketActions.CheckGift(await Ticket(ticketId, ct), Line(lineId), Payload<GiftLineRequest>(payloadJson));
            case ApprovalKind.PriceChange:
                return TicketActions.CheckPriceChange(await Ticket(ticketId, ct), Line(lineId), Payload<ChangePriceRequest>(payloadJson));
            case ApprovalKind.Discount:
                return TicketActions.CheckDiscount(await Ticket(ticketId, ct), Payload<DiscountRequest>(payloadJson));
            case ApprovalKind.Reprint:
            {
                var ticket = await Ticket(ticketId, ct);
                if (!ticket.Lines.Any(l => l.Counts)) throw new DomainException("There is nothing on this ticket to bill.");
                var reason = ActionReasons.FreeText(Payload<ReprintRequest>(payloadJson).Reason, required: false);
                return new ActionSummary("Reprint bill", ticket.Total, reason.Length == 0 ? null : reason, null);
            }
            case ApprovalKind.Move:
            {
                var ticket = await move.Load(ticketId, ct);
                var to = await move.Check(ticket, Payload<MoveTicketRequest>(payloadJson), terminalId, ct);
                return new ActionSummary($"Move to {Label(to.Name)}", ticket.Total, null, null);
            }
            case ApprovalKind.Merge:
            {
                var request = Payload<MergeTicketsRequest>(payloadJson);
                if (request.IntoTicketId != ticketId) throw new DomainException("A merge request belongs to the ticket that is kept.");
                var found = await merge.Load(MergeTickets.Ids(request), ct);
                await merge.Check(found, request, terminalId, ct);
                var others = found.Where(k => k.Id != ticketId).OrderBy(k => k.Number).Select(k => $"#{k.Number}");
                return new ActionSummary($"Merge {string.Join(", ", others)} into #{found.Single(k => k.Id == ticketId).Number}",
                    found.Sum(k => k.Total), null, null);
            }
            default:
                throw new DomainException("Unknown request.");
        }
    }

    /// <summary>Does the action for the requester, approved by <paramref name="actor"/>'s approver. The caller saves.</summary>
    public async Task<AfterApproval> Apply(ApprovalRequest request, TicketActor actor, CancellationToken ct)
    {
        var json = request.PayloadJson;
        switch (request.Kind)
        {
            case ApprovalKind.Void:
            {
                var ticket = await Ticket(request.TicketId, ct);
                await actions.Void(ticket, Line(request.OrderLineId), Payload<VoidLineRequest>(json), actor, ct);
                return new AfterApproval([ticket], []);
            }
            case ApprovalKind.Gift:
            {
                var ticket = await Ticket(request.TicketId, ct);
                await actions.Gift(ticket, Line(request.OrderLineId), Payload<GiftLineRequest>(json), actor, ct);
                return new AfterApproval([ticket], []);
            }
            case ApprovalKind.PriceChange:
            {
                var ticket = await Ticket(request.TicketId, ct);
                await actions.ChangePrice(ticket, Line(request.OrderLineId), Payload<ChangePriceRequest>(json), actor, ct);
                return new AfterApproval([ticket], []);
            }
            case ApprovalKind.Discount:
            {
                var ticket = await Ticket(request.TicketId, ct);
                await actions.Discount(ticket, Payload<DiscountRequest>(json), actor, ct);
                return new AfterApproval([ticket], []);
            }
            case ApprovalKind.Reprint:
            {
                // The bill prints on the requester's terminal.
                var ticket = await Ticket(request.TicketId, ct);
                await prints.Reprint(ticket, actor.TerminalId, actor.ApprovedById, ct);
                return new AfterApproval([ticket], []);
            }
            case ApprovalKind.Move:
            {
                var ticket = await move.Load(request.TicketId, ct);
                var to = await move.Check(ticket, Payload<MoveTicketRequest>(json), actor.TerminalId, ct);
                var from = move.Apply(ticket, to, actor.UserId, actor.TerminalId, actor.ApprovedById);
                return new AfterApproval([ticket], [], from?.Id);
            }
            case ApprovalKind.Merge:
            {
                var payload = Payload<MergeTicketsRequest>(json);
                var found = await merge.Load(MergeTickets.Ids(payload), ct);
                await merge.Check(found, payload, actor.TerminalId, ct);
                var (into, others) = merge.Apply(found, payload, actor.UserId, actor.TerminalId, actor.ApprovedById);
                return new AfterApproval([into, .. others], others.Select(k => k.Id).ToList());
            }
            default:
                throw new DomainException("Unknown request.");
        }
    }

    private async Task<Ticket> Ticket(Guid id, CancellationToken ct)
    {
        var ticket = await db.Tickets.Include(k => k.Lines).Include(k => k.Table).FirstOrDefaultAsync(k => k.Id == id, ct)
            ?? throw new NotFoundException("Ticket not found.");
        if (!ticket.IsOpen) throw new DomainException($"#{ticket.Number} is no longer open.");
        return ticket;
    }

    private static Guid Line(Guid? lineId) => lineId ?? throw new DomainException("Choose the line first.");

    private static string Label(string table) => table.All(char.IsDigit) ? $"Table {table}" : table;
}

/// <summary>The tickets an approved action changed, the ones merged away, and the table a move left.</summary>
public sealed record AfterApproval(List<Ticket> Changed, List<Guid> MergedAway, Guid? MovedFromTableId = null);

/// <summary>
/// Sends a request for an action the requester may only do with approval. Allowed users do the
/// action themselves; NotAllowed users can't ask. The action is checked now (and again when it is
/// approved); a line or ticket can have only one request of a kind waiting. With
/// <c>pos.approvalsFromOwnCounter</c> off nobody can send a request (a supervisor enters their PIN on
/// the spot); requests already waiting can still be decided.
/// </summary>
public sealed class CreateApprovalRequest(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, ITicketDatabase tickets,
    ApprovalActions actions, Approvers approvers, TicketAccess access, IAuditLog audit, IPosEvents events, TicketEvents ticketEvents,
    TimeProvider clock)
{
    public async Task<ApprovalRequestDto> Execute(CreateApprovalRequestDto dto, CancellationToken ct)
    {
        var user = session.RequireUser();
        if (user.TerminalId is not { } terminalId)
            throw new ForbiddenException("Requests are sent from a terminal.", "terminal_required");
        if (!(await PosSettings.Load(db, ct)).ApprovalsFromOwnCounter)
            throw new ForbiddenException("Requests are switched off. Ask a supervisor to enter their PIN here.", ApprovalCodes.RequestsOff);
        var key = ApprovalKinds.Permission(dto.Kind);
        switch (await permissions.Check(user, key, ct))
        {
            case PermissionValue.NotAllowed:
                throw new PermissionDeniedException(key);
            case PermissionValue.Allowed:
                throw new ConflictException("You can do this yourself; no approval is needed.", ApprovalCodes.NotNeeded);
        }
        if (dto.PreferredApproverId is { } preferred && !await approvers.CanDecide(preferred, dto.Kind, user.UserId, ct))
            throw new DomainException("That person can't approve this. Choose someone else.");

        var payload = dto.Payload.ValueKind == JsonValueKind.Undefined ? "{}" : dto.Payload.GetRawText();
        var day = await WorkPeriods.Current(db, ct) ?? throw new DomainException("The day isn't open.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticketIds = ApprovalActions.TicketIds(dto.Kind, dto.TicketId, payload);
        await tickets.LockRows(ticketIds, ct);
        // Only for tickets the requester may work on (their own, or any with ticket.others).
        foreach (var affected in await db.Tickets.AsNoTracking().Where(k => ticketIds.Contains(k.Id)).ToListAsync(ct))
            await access.EnsureMayChange(user, affected, ct);
        if (dto.Kind == ApprovalKind.Move
            && ApprovalActions.Payload<MoveTicketRequest>(payload) is { } move
            && await db.Tables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == move.ToTableId, ct) is { } to)
            await access.EnsureTableFree(user, to.Id, to.Name, dto.TicketId, ct);
        var summary = await actions.Check(dto.Kind, dto.TicketId, dto.LineId, payload, terminalId, ct);
        await PendingRequests.EnsureNone(db, dto.TicketId, summary.LineId, dto.Kind, null, ct);

        var now = clock.GetUtcNow();
        var request = new ApprovalRequest
        {
            Kind = dto.Kind, TicketId = dto.TicketId, OrderLineId = summary.LineId, PayloadJson = payload,
            Summary = summary.Text, Amount = summary.Amount, Reason = summary.Reason,
            RequestedByUserId = user.UserId, TerminalId = terminalId, RequestedAt = now,
            PreferredApproverId = dto.PreferredApproverId, WorkPeriodId = day.Id,
        };
        db.ApprovalRequests.Add(request);
        audit.Write(new AuditRecord(AuditActions.ApprovalRequested, nameof(ApprovalRequest), request.Id.ToString(),
            NewValue: new { kind = dto.Kind.ToString(), request.Summary, request.Amount, request.Reason, ticketId = dto.TicketId, preferred = dto.PreferredApproverId }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await events.Publish(PosEventNames.ApprovalRequested, new
        {
            requestId = request.Id,
            kind = request.Kind.ToString(),
            ticketId = request.TicketId,
            requestedBy = user.Name,
            summary = request.Summary,
            preferredApproverId = request.PreferredApproverId,
        }, ct);
        var ticket = await db.Tickets.AsNoTracking().FirstAsync(k => k.Id == dto.TicketId, ct);
        await ticketEvents.Changed(ticket, ticket.TableId, ct);
        return (await ApprovalList.Load(db, [request.Id], user, permissions, ct)).Single();
    }
}

/// <summary>
/// A supervisor approves a request. The approver must be Allowed for the action and must not be
/// the requester. The request is claimed with a conditional update, so of two supervisors
/// approving at once exactly one wins; the action then runs in the same transaction for the
/// requester. If it no longer fits (the line was voided meanwhile, the ticket closed), nothing
/// changes and the request becomes Expired with the reason.
/// </summary>
/// <summary>
/// The approver's answers. <c>WasPoured</c>, for a void with <c>stock.returnOnVoidIfNotServed</c> on:
/// the approver confirms or changes the requester's "Was it already poured?", and theirs is stored.
/// </summary>
public sealed record ApproveRequestDto(bool? WasPoured = null);

public sealed class ApproveRequest(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, ITicketDatabase tickets,
    ApprovalActions actions, IAuditLog audit, IPosEvents events, TicketEvents ticketEvents, TimeProvider clock)
{
    public async Task<ApprovalRequestDto> Execute(Guid id, CancellationToken ct, ApproveRequestDto? decision = null)
    {
        var approver = session.RequireUser();
        var request = await Decidable.Load(db, permissions, approver, id, ct);
        if (request.Kind == ApprovalKind.Void && decision?.WasPoured is { } poured
            && (await SettingsRegistry.Load(db, ct)).Bool(SettingsRegistry.StockReturnOnVoidIfNotServed))
        {
            request.PayloadJson = ApprovalActions.Serialize(ApprovalActions.Payload<VoidLineRequest>(request.PayloadJson) with { WasPoured = poured });
            await db.ApprovalRequests.Where(r => r.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.PayloadJson, request.PayloadJson), ct);
        }
        var requester = await db.Users.AsNoTracking().FirstAsync(u => u.Id == request.RequestedByUserId, ct);
        var actor = new TicketActor(requester.Id, requester.Name, request.TerminalId, approver.UserId, approver.Name, request.Id);

        AfterApproval after;
        var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await tickets.LockRows(ApprovalActions.TicketIds(request.Kind, request.TicketId, request.PayloadJson), ct);
            var now = clock.GetUtcNow();
            var claimed = await db.ApprovalRequests
                .Where(r => r.Id == id && r.Status == ApprovalStatus.Pending)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, ApprovalStatus.Approved)
                    .SetProperty(r => r.DecidedByUserId, approver.UserId)
                    .SetProperty(r => r.DecidedAt, now)
                    .SetProperty(r => r.RowVersion, r => r.RowVersion + 1), ct);
            if (claimed != 1)
                throw new ConflictException("Someone else has decided this request already.", ApprovalCodes.AlreadyDecided);

            await PendingRequests.EnsureNone(db, request.TicketId, request.OrderLineId, request.Kind, request.Id, ct);
            after = await actions.Apply(request, actor, ct);
            var expired = await ApprovalExpiry.ForTickets(db, after.MergedAway, ApprovalExpiry.TicketMerged, now, ct);
            audit.Write(new AuditRecord(AuditActions.ApprovalApproved, nameof(ApprovalRequest), request.Id.ToString(),
                new { status = ApprovalStatus.Pending.ToString() },
                new { status = ApprovalStatus.Approved.ToString(), kind = request.Kind.ToString(), request.Summary, requestedBy = requester.Name },
                approver.UserId));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await tx.DisposeAsync();
            await ApprovalExpiry.Publish(events, expired, ct);
        }
        catch (Exception ex) when (ex is DomainException or NotFoundException
            || (ex is ConflictException c && c.Code != ApprovalCodes.AlreadyDecided))
        {
            await tx.DisposeAsync();
            await ExpireBecause(id, ex.Message, ct);
            throw new ConflictException($"This can't be approved any more: {ex.Message}", ApprovalCodes.Expired);
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }

        request.Status = ApprovalStatus.Approved;
        await ApprovalExpiry.Publish(events, [ApprovalExpiry.Resolved(request, approver.Name)], ct);
        foreach (var ticket in after.Changed) await ticketEvents.Changed(ticket, ticket.TableId, ct);
        if (after.MovedFromTableId is { } from) await ticketEvents.Changed(after.Changed[0], from, ct);
        return (await ApprovalList.Load(db, [id], approver, permissions, ct)).Single();
    }

    private async Task ExpireBecause(Guid id, string reason, CancellationToken ct)
    {
        (db as DbContext)?.ChangeTracker.Clear();
        var request = await db.ApprovalRequests.FirstOrDefaultAsync(r => r.Id == id && r.Status == ApprovalStatus.Pending, ct);
        if (request is null) return;
        var resolved = ApprovalExpiry.Expire(request, reason, clock.GetUtcNow());
        audit.Write(new AuditRecord(AuditActions.ApprovalExpired, nameof(ApprovalRequest), id.ToString(),
            NewValue: new { request.Summary, note = reason }));
        try
        {
            await db.SaveChangesAsync(ct);
            await ApprovalExpiry.Publish(events, [resolved], ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Decided by someone else meanwhile.
        }
    }
}

/// <summary>Rejects a request: nothing changes on the ticket. The requester sees it live.</summary>
public sealed class RejectRequest(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IAuditLog audit, IPosEvents events,
    TicketEvents ticketEvents, TimeProvider clock)
{
    public async Task<ApprovalRequestDto> Execute(Guid id, RejectApprovalRequestDto dto, CancellationToken ct)
    {
        var approver = session.RequireUser();
        var note = ActionReasons.Note(dto?.Note);
        var request = await Decidable.Load(db, permissions, approver, id, ct);
        var decided = await Decidable.Decide(db, request, ApprovalStatus.Rejected, approver.UserId, note, clock.GetUtcNow(), ct);
        if (!decided) throw new ConflictException("Someone else has decided this request already.", ApprovalCodes.AlreadyDecided);
        audit.Write(new AuditRecord(AuditActions.ApprovalRejected, nameof(ApprovalRequest), id.ToString(),
            new { status = ApprovalStatus.Pending.ToString() },
            new { status = ApprovalStatus.Rejected.ToString(), request.Summary, note }));
        await db.SaveChangesAsync(ct);

        request.Status = ApprovalStatus.Rejected;
        request.DecisionNote = note;
        await ApprovalExpiry.Publish(events, [ApprovalExpiry.Resolved(request, approver.Name)], ct);
        await Decidable.TicketChanged(db, ticketEvents, request.TicketId, ct);
        return (await ApprovalList.Load(db, [id], approver, permissions, ct)).Single();
    }
}

/// <summary>The requester takes back their own pending request.</summary>
public sealed class CancelRequest(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IAuditLog audit, IPosEvents events,
    TicketEvents ticketEvents, TimeProvider clock)
{
    public async Task<ApprovalRequestDto> Execute(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        var request = await db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Request not found.");
        if (request.RequestedByUserId != user.UserId)
            throw new ForbiddenException("Only the person who asked can cancel a request.", ApprovalCodes.OwnRequest);
        var decided = await Decidable.Decide(db, request, ApprovalStatus.Cancelled, user.UserId, null, clock.GetUtcNow(), ct);
        if (!decided) throw new ConflictException("This request has been decided already.", ApprovalCodes.AlreadyDecided);
        audit.Write(new AuditRecord(AuditActions.ApprovalCancelled, nameof(ApprovalRequest), id.ToString(),
            new { status = ApprovalStatus.Pending.ToString() }, new { status = ApprovalStatus.Cancelled.ToString(), request.Summary }));
        await db.SaveChangesAsync(ct);

        request.Status = ApprovalStatus.Cancelled;
        await ApprovalExpiry.Publish(events, [ApprovalExpiry.Resolved(request, user.Name)], ct);
        await Decidable.TicketChanged(db, ticketEvents, request.TicketId, ct);
        return (await ApprovalList.Load(db, [id], user, permissions, ct)).Single();
    }
}

internal static class Decidable
{
    /// <summary>A pending request this user may decide: Allowed for the action, and not their own.</summary>
    public static async Task<ApprovalRequest> Load(ITausiDb db, IPermissionService permissions, SessionUser approver, Guid id, CancellationToken ct)
    {
        var request = await db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Request not found.");
        if (request.RequestedByUserId == approver.UserId)
            throw new ForbiddenException("You can't approve your own request. Ask a supervisor.", ApprovalCodes.OwnRequest);
        if (await permissions.Check(approver, ApprovalKinds.Permission(request.Kind), ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(ApprovalKinds.Permission(request.Kind));
        if (!request.IsPending)
            throw new ConflictException($"This request is already {request.Status.ToString().ToLowerInvariant()}.", ApprovalCodes.AlreadyDecided);
        return request;
    }

    /// <summary>Pending to <paramref name="status"/>, only if still pending (so one decision wins).</summary>
    public static async Task<bool> Decide(ITausiDb db, ApprovalRequest request, ApprovalStatus status, Guid by, string? note,
        DateTimeOffset now, CancellationToken ct) =>
        await db.ApprovalRequests
            .Where(r => r.Id == request.Id && r.Status == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.DecidedByUserId, by)
                .SetProperty(r => r.DecidedAt, now)
                .SetProperty(r => r.DecisionNote, note)
                .SetProperty(r => r.RowVersion, r => r.RowVersion + 1), ct) == 1;

    public static async Task TicketChanged(ITausiDb db, TicketEvents events, Guid ticketId, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(k => k.Id == ticketId, ct);
        if (ticket is not null) await events.Changed(ticket, ticket.TableId, ct);
    }
}

/// <summary>Task 8's settle must call this: a ticket with any request waiting can't be settled.</summary>
public static class SettleChecks
{
    public static async Task EnsureNoPendingRequests(ITausiDb db, Guid ticketId, CancellationToken ct)
    {
        var pending = await db.ApprovalRequests.AsNoTracking()
            .Where(r => r.TicketId == ticketId && r.Status == ApprovalStatus.Pending)
            .OrderBy(r => r.RequestedAt)
            .Select(r => new
            {
                r.Kind,
                r.Summary,
                To = db.Users.Where(u => u.Id == r.PreferredApproverId).Select(u => u.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);
        if (pending.Count == 0) return;
        // "Waiting for SAM to approve a void on Guiness" (or "a supervisor" when sent to anyone).
        var first = pending[0];
        var on = first.Summary.Contains(" × ") ? first.Summary[(first.Summary.IndexOf(" × ", StringComparison.Ordinal) + 3)..] : null;
        var what = ApprovalKinds.Noun(first.Kind);
        var text = $"Waiting for {first.To ?? "a supervisor"} to approve {(what is "void" or "gift" or "price change" ? "a " : "the ")}{what}"
            + (on is null ? "" : $" on {on}") + (pending.Count > 1 ? $" (and {pending.Count - 1} more)" : "") + ".";
        throw new ConflictException(text, ApprovalCodes.RequestsPending);
    }
}
