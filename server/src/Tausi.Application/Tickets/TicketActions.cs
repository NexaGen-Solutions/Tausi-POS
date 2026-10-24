using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;
using Tausi.Domain.Service;

namespace Tausi.Application.Tickets;

public static class ActionCodes
{
    public const string RequestPending = "request_pending";
    public const string NotVoidable = "not_voidable";
}

/// <summary>
/// Requests still waiting for a decision block other changes to what they concern: a line with a
/// pending void, gift or price change can't be voided, gifted, changed or taken off, and a ticket
/// can have one pending request of each ticket-level kind.
/// </summary>
public static class PendingRequests
{
    public static async Task EnsureNone(ITausiDb db, Guid ticketId, Guid? lineId, ApprovalKind kind, Guid? except, CancellationToken ct)
    {
        var pending = await db.ApprovalRequests.AsNoTracking()
            .Where(r => r.TicketId == ticketId && r.Status == ApprovalStatus.Pending && r.Id != except)
            .Where(r => (lineId != null && r.OrderLineId == lineId) || (r.OrderLineId == null && r.Kind == kind && lineId == null))
            .Select(r => new { r.Kind, r.Summary })
            .FirstOrDefaultAsync(ct);
        if (pending is not null)
            throw new ConflictException(
                $"{pending.Summary} is waiting for approval. Wait for the decision, or cancel the request first.",
                ActionCodes.RequestPending);
    }

    /// <summary>For taking a New line off or changing its quantity.</summary>
    public static async Task EnsureNoneOnLine(ITausiDb db, Guid ticketId, Guid lineId, CancellationToken ct)
    {
        var pending = await db.ApprovalRequests.AsNoTracking()
            .Where(r => r.TicketId == ticketId && r.OrderLineId == lineId && r.Status == ApprovalStatus.Pending)
            .Select(r => r.Summary).FirstOrDefaultAsync(ct);
        if (pending is not null)
            throw new ConflictException($"{pending} is waiting for approval. Wait for the decision, or cancel the request first.",
                ActionCodes.RequestPending);
    }
}

/// <summary>
/// Void, gift, discount and price change on a ticket whose row is locked by the caller. Each has a
/// Check (used when a request is made, and again when it is approved) and an Apply. Apply works on
/// the tracked ticket and adds logs, audit and print jobs to the caller's unit of work; the caller
/// saves and commits. Print jobs never undo the change: without a usable printer nothing prints.
/// </summary>
public sealed class TicketActions(ITausiDb db, TicketPrints prints, IAuditLog audit, TimeProvider clock)
{
    // ---- Void (printed lines only) ----

    public static ActionSummary CheckVoid(Ticket ticket, Guid lineId, VoidLineRequest r)
    {
        var line = Line(ticket, lineId);
        if (line.Status == OrderLineStatus.New)
            throw new DomainException($"{line.Name} isn't printed yet: take it off with Remove instead.", ActionCodes.NotVoidable);
        if (!line.Counts || line.Status != OrderLineStatus.Printed)
            throw new DomainException($"{line.Name} was already taken off.", ActionCodes.NotVoidable);
        CheckQuantity(line, r.Quantity);
        var reason = ActionReasons.Text(ActionReasons.Void, r.Reason, r.Note);
        return new ActionSummary($"Void {Qty(r.Quantity)} × {line.Name}", Money(line.UnitPrice * r.Quantity), reason, line.Id);
    }

    public async Task Void(Ticket ticket, Guid lineId, VoidLineRequest r, TicketActor actor, CancellationToken ct)
    {
        var summary = CheckVoid(ticket, lineId, r);
        var now = clock.GetUtcNow();
        var part = Split(ticket, Line(ticket, lineId), r.Quantity);
        part.Status = OrderLineStatus.Voided;
        part.VoidedAt = now;
        part.VoidedByUserId = actor.UserId;
        part.VoidReason = summary.Reason;
        ticket.SpreadDiscount();
        ticket.Touch(now);

        // With stock.returnOnVoidIfNotServed off nobody is asked: every void counts as poured.
        var returnsStock = (await SettingsRegistry.Load(db, ct)).Bool(SettingsRegistry.StockReturnOnVoidIfNotServed);
        var poured = !returnsStock
            || string.Equals(r.Reason?.Trim(), ActionReasons.SpilledOrBroken, StringComparison.OrdinalIgnoreCase) || r.WasPoured == true;
        db.VoidLogs.Add(new VoidLog
        {
            TicketId = ticket.Id, OrderLineId = part.Id, MenuItemId = part.MenuItemId, ItemName = part.Name,
            Quantity = part.Quantity, UnitPrice = part.UnitPrice, Amount = part.Gross,
            Reason = summary.Reason!, Note = ActionReasons.Note(r.Note), WasPoured = poured,
            RequestedByUserId = actor.UserId, ApprovedByUserId = actor.ApprovedById, TerminalId = actor.TerminalId, At = now,
            ApprovalRequestId = actor.RequestId,
        });
        var slip = await prints.VoidSlip(ticket, part, actor.TerminalId, summary.Reason!, actor.UserName, actor.ApproverName, ct);
        var bill = await prints.CorrectedBillIfBilled(ticket, actor.TerminalId, ct);
        audit.Write(new AuditRecord(AuditActions.LineVoided, nameof(Ticket), ticket.Id.ToString(),
            new { ticket = ticket.Number, line = part.Name, part.Options, quantity = part.Quantity, amount = part.Gross },
            new { reason = summary.Reason, wasPoured = poured, total = ticket.Total, voidSlip = slip?.Id, correctedBill = bill?.Id, request = actor.RequestId },
            actor.ApprovedById, actor.UserId, actor.TerminalId));
    }

    // ---- Gift (New or Printed lines that aren't voided) ----

    public static ActionSummary CheckGift(Ticket ticket, Guid lineId, GiftLineRequest r)
    {
        var line = Changeable(ticket, lineId, "gifted");
        CheckQuantity(line, r.Quantity);
        var reason = ActionReasons.Text(ActionReasons.Gift, r.Reason, r.Note);
        return new ActionSummary($"Gift {Qty(r.Quantity)} × {line.Name}", Money(line.UnitPrice * r.Quantity), reason, line.Id);
    }

    public async Task Gift(Ticket ticket, Guid lineId, GiftLineRequest r, TicketActor actor, CancellationToken ct)
    {
        var summary = CheckGift(ticket, lineId, r);
        var now = clock.GetUtcNow();
        var part = Split(ticket, Line(ticket, lineId), r.Quantity);
        part.IsGift = true;
        ticket.SpreadDiscount();
        ticket.Touch(now);
        var worth = Money(part.UnitPrice * part.Quantity);
        db.GiftLogs.Add(new GiftLog
        {
            TicketId = ticket.Id, OrderLineId = part.Id, MenuItemId = part.MenuItemId, ItemName = part.Name,
            Quantity = part.Quantity, UnitPrice = part.UnitPrice, Amount = worth,
            Reason = summary.Reason!, Note = ActionReasons.Note(r.Note),
            RequestedByUserId = actor.UserId, ApprovedByUserId = actor.ApprovedById, TerminalId = actor.TerminalId, At = now,
            ApprovalRequestId = actor.RequestId,
        });
        var bill = await prints.CorrectedBillIfBilled(ticket, actor.TerminalId, ct);
        audit.Write(new AuditRecord(AuditActions.LineGifted, nameof(Ticket), ticket.Id.ToString(),
            new { ticket = ticket.Number, line = part.Name, part.Options, quantity = part.Quantity, amount = worth },
            new { reason = summary.Reason, total = ticket.Total, correctedBill = bill?.Id, request = actor.RequestId },
            actor.ApprovedById, actor.UserId, actor.TerminalId));
    }

    // ---- Discount (ticket level, one per ticket) ----

    public static ActionSummary CheckDiscount(Ticket ticket, DiscountRequest r)
    {
        if (r.Kind is null)
        {
            if (ticket.DiscountKind is null) throw new DomainException("This ticket has no discount.");
            return new ActionSummary("Remove discount", ticket.DiscountTotal, null, null);
        }
        var reason = ActionReasons.Text(ActionReasons.Discount, r.Reason, r.Note);
        var basis = ticket.DiscountableLines.Sum(l => l.Gross);
        if (basis <= 0) throw new DomainException("There is nothing on this ticket to discount.");
        if (r.Value <= 0) throw new DomainException("The discount must be more than 0.");
        if (r.Kind == DiscountKind.Percent && r.Value > 100) throw new DomainException("A discount can be at most 100%.");
        if (r.Kind == DiscountKind.Amount && r.Value > basis)
            throw new DomainException($"The discount can't be more than the total of {Money(basis):#,##0.##}.");
        var amount = r.Kind == DiscountKind.Percent ? Money(basis * r.Value / 100m) : r.Value;
        var text = r.Kind == DiscountKind.Percent
            ? $"Discount {r.Value.ToString("0.##", CultureInfo.InvariantCulture)}%"
            : $"Discount {amount.ToString("#,##0.##", CultureInfo.InvariantCulture)}";
        return new ActionSummary(text, amount, reason, null);
    }

    public async Task Discount(Ticket ticket, DiscountRequest r, TicketActor actor, CancellationToken ct)
    {
        var summary = CheckDiscount(ticket, r);
        var now = clock.GetUtcNow();
        var (oldKind, oldValue, oldAmount) = (ticket.DiscountKind, ticket.DiscountValue, ticket.DiscountTotal);
        ticket.SetDiscount(r.Kind, r.Value, summary.Reason);
        ticket.Touch(now);
        db.DiscountLogs.Add(new DiscountLog
        {
            TicketId = ticket.Id, Kind = r.Kind, Value = r.Kind is null ? 0 : r.Value, Amount = ticket.DiscountTotal,
            OldKind = oldKind, OldValue = oldValue, Reason = summary.Reason, Note = ActionReasons.Note(r.Note),
            RequestedByUserId = actor.UserId, ApprovedByUserId = actor.ApprovedById, TerminalId = actor.TerminalId, At = now,
            ApprovalRequestId = actor.RequestId,
        });
        var bill = await prints.CorrectedBillIfBilled(ticket, actor.TerminalId, ct);
        audit.Write(new AuditRecord(AuditActions.DiscountChanged, nameof(Ticket), ticket.Id.ToString(),
            new { ticket = ticket.Number, kind = oldKind?.ToString(), value = oldValue, amount = oldAmount },
            new { kind = r.Kind?.ToString(), value = ticket.DiscountValue, amount = ticket.DiscountTotal, reason = summary.Reason,
                total = ticket.Total, correctedBill = bill?.Id, request = actor.RequestId },
            actor.ApprovedById, actor.UserId, actor.TerminalId));
    }

    // ---- Price change on the ticket (order.change_price, not menu.edit_prices) ----

    public const decimal MaxUnitPrice = 10_000_000m;

    public static ActionSummary CheckPriceChange(Ticket ticket, Guid lineId, ChangePriceRequest r)
    {
        var line = Changeable(ticket, lineId, "changed");
        CheckQuantity(line, r.Quantity);
        if (r.NewUnitPrice < 0 || r.NewUnitPrice > MaxUnitPrice || decimal.Round(r.NewUnitPrice, 2) != r.NewUnitPrice)
            throw new DomainException("Enter a price of 0 or more, to the cent.");
        if (r.NewUnitPrice == line.UnitPrice) throw new DomainException($"{line.Name} is already {Money(line.UnitPrice):#,##0.##}.");
        var reason = ActionReasons.FreeText(r.Reason, required: true);
        return new ActionSummary(
            $"Price {Qty(r.Quantity)} × {line.Name}: {line.UnitPrice:#,##0.##} → {r.NewUnitPrice:#,##0.##}",
            Money(r.NewUnitPrice * r.Quantity), reason, line.Id);
    }

    public async Task ChangePrice(Ticket ticket, Guid lineId, ChangePriceRequest r, TicketActor actor, CancellationToken ct)
    {
        var summary = CheckPriceChange(ticket, lineId, r);
        var now = clock.GetUtcNow();
        var part = Split(ticket, Line(ticket, lineId), r.Quantity);
        var old = part.UnitPrice;
        part.OriginalUnitPrice ??= old;
        part.UnitPrice = r.NewUnitPrice;
        ticket.SpreadDiscount();
        ticket.Touch(now);
        db.PriceChangeLogs.Add(new PriceChangeLog
        {
            Kind = PriceChangeKind.Ticket, Source = PriceChangeSources.Ticket, MenuItemId = part.MenuItemId,
            TicketId = ticket.Id, OrderLineId = part.Id, OldPrice = old, NewPrice = r.NewUnitPrice, Reason = summary.Reason,
            ChangedByUserId = actor.UserId, ApprovedByUserId = actor.ApprovedById, TerminalId = actor.TerminalId, At = now,
        });
        var bill = await prints.CorrectedBillIfBilled(ticket, actor.TerminalId, ct);
        audit.Write(new AuditRecord(AuditActions.LinePriceChanged, nameof(Ticket), ticket.Id.ToString(),
            new { ticket = ticket.Number, line = part.Name, quantity = part.Quantity, unitPrice = old },
            new { unitPrice = r.NewUnitPrice, reason = summary.Reason, total = ticket.Total, correctedBill = bill?.Id, request = actor.RequestId },
            actor.ApprovedById, actor.UserId, actor.TerminalId));
    }

    // ---- Shared ----

    private static OrderLine Line(Ticket ticket, Guid lineId) =>
        ticket.Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new NotFoundException("That line is not on this ticket.");

    private static OrderLine Changeable(Ticket ticket, Guid lineId, string what)
    {
        var line = Line(ticket, lineId);
        if (!line.Counts) throw new DomainException($"{line.Name} was already taken off.", ActionCodes.NotVoidable);
        if (line.IsGift) throw new DomainException($"{line.Name} is a gift and can't be {what}.", ActionCodes.NotVoidable);
        return line;
    }

    private static void CheckQuantity(OrderLine line, decimal quantity)
    {
        if (quantity <= 0 || quantity > line.Quantity)
            throw new DomainException($"Choose a quantity from 1 to {Qty(line.Quantity)}.");
    }

    /// <summary>The whole line, or a new line split off it (added to the ticket and the unit of work).</summary>
    private OrderLine Split(Ticket ticket, OrderLine line, decimal quantity)
    {
        var part = line.SplitOff(quantity);
        if (part != line)
        {
            ticket.Lines.Add(part);
            db.OrderLines.Add(part);
        }
        return part;
    }

    private static decimal Money(decimal amount) => Domain.Money.MoneyMath.Round(amount);

    private static string Qty(decimal q) => q.ToString("0.##", CultureInfo.InvariantCulture);
}
