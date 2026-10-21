using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Tickets;

public sealed record AddLineRequest(Guid MenuItemId, IReadOnlyList<Guid>? OptionIds, decimal Quantity, string? Note = null);

public sealed record ChangeLineRequest(decimal Quantity);

public sealed record UpdateTicketRequest(string? Note, string? CustomerName);

/// <summary>Adding lines, shared by the first save and later adds.</summary>
public static class TicketLines
{
    public const decimal MaxQuantity = 999;

    public static void CheckQuantity(decimal quantity)
    {
        if (quantity <= 0 || quantity > MaxQuantity)
            throw new DomainException($"The quantity must be more than 0 and at most {MaxQuantity}.");
    }

    public static string? CleanNote(string? note)
    {
        var clean = note?.Trim();
        if (clean is { Length: > OrderLine.MaxNoteLength })
            throw new DomainException($"A line note can be at most {OrderLine.MaxNoteLength} characters.");
        return string.IsNullOrEmpty(clean) ? null : clean;
    }

    /// <summary>
    /// Adds the item, or adds to the quantity of this user's New line with the same item, options,
    /// price and no note. Printed lines and other people's lines are never changed.
    /// </summary>
    public static (OrderLine Line, bool Created) Add(
        Ticket ticket, PricedItem item, decimal quantity, string? note, Guid userId, Guid terminalId, DateTimeOffset now)
    {
        CheckQuantity(quantity);
        var same = note is null
            ? ticket.Lines.FirstOrDefault(l => l.Status == OrderLineStatus.New && l.MenuItemId == item.MenuItemId
                && l.OptionIds == item.OptionIds && l.UnitPrice == item.UnitPrice && l.AddedByUserId == userId && l.Note is null)
            : null;
        if (same is not null)
        {
            CheckQuantity(same.Quantity + quantity);
            same.SetQuantity(same.Quantity + quantity);
            return (same, false);
        }

        var line = new OrderLine
        {
            TicketId = ticket.Id,
            MenuItemId = item.MenuItemId,
            Name = item.Name,
            ShortName = item.ShortName,
            Options = item.Options,
            OptionIds = item.OptionIds,
            Note = note,
            UnitPrice = item.UnitPrice,
            VatRate = item.VatRate,
            VatExempt = item.VatExempt,
            AddedByUserId = userId,
            AddedTerminalId = terminalId,
            AddedAt = now,
        };
        line.SetQuantity(quantity);
        ticket.Lines.Add(line);
        return (line, true);
    }
}

/// <summary>Adds an item to a saved ticket, as it happens (nothing waits on the terminal).</summary>
public sealed class AddLine(
    ITausiDb db, TicketEditing editing, IPermissionService permissions, IApprovalTokenSource approval,
    MenuPricing pricing, TicketEvents events, TimeProvider clock)
{
    public async Task<TicketDetailDto> Execute(Guid ticketId, AddLineRequest request, CancellationToken ct)
    {
        await using var edit = await editing.Open(ticketId, ct);
        await permissions.Require(edit.User, PermissionKeys.OrderAdd, approval.ApprovalToken, ct);
        var priced = await (await pricing.For(edit.TerminalId, ct)).Price(request.MenuItemId, request.OptionIds, ct);
        var now = clock.GetUtcNow();
        var (line, created) = TicketLines.Add(edit.Ticket, priced, request.Quantity, TicketLines.CleanNote(request.Note),
            edit.User.UserId, edit.TerminalId, now);
        // Ids are made in code, so EF would take a new line reached through a saved ticket for an existing row.
        if (created) db.OrderLines.Add(line);
        edit.Ticket.SpreadDiscount();
        edit.Ticket.Touch(now);
        await db.SaveChangesAsync(ct);
        await edit.Transaction.CommitAsync(ct);
        await events.Changed(edit.Ticket, edit.Ticket.TableId, ct);
        return await TicketDetails.Mine(db, edit.Ticket, edit.User, ct);
    }
}

/// <summary>
/// Changes a New line's quantity. Taking it to 0 cancels it, which needs
/// <c>order.cancel_unprinted</c>. Printed lines change only through a void, gift or price change.
/// </summary>
public sealed class ChangeLine(
    ITausiDb db, TicketEditing editing, IPermissionService permissions, IApprovalTokenSource approval,
    IAuditLog audit, TicketEvents events, TimeProvider clock)
{
    public async Task<TicketDetailDto> Quantity(Guid ticketId, Guid lineId, ChangeLineRequest request, CancellationToken ct)
    {
        if (request.Quantity < 0) throw new DomainException("The quantity can't be below 0.");
        await using var edit = await editing.Open(ticketId, ct);
        var line = NewLine(edit.Ticket, lineId);
        await PendingRequests.EnsureNoneOnLine(db, ticketId, lineId, ct);
        if (request.Quantity == 0)
            await Cancel(edit, line, ct);
        else
        {
            await permissions.Require(edit.User, PermissionKeys.OrderAdd, approval.ApprovalToken, ct);
            TicketLines.CheckQuantity(request.Quantity);
            line.SetQuantity(request.Quantity);
        }
        return await Save(edit, ct);
    }

    public async Task<TicketDetailDto> Remove(Guid ticketId, Guid lineId, CancellationToken ct)
    {
        await using var edit = await editing.Open(ticketId, ct);
        var line = NewLine(edit.Ticket, lineId);
        await PendingRequests.EnsureNoneOnLine(db, ticketId, lineId, ct);
        await Cancel(edit, line, ct);
        return await Save(edit, ct);
    }

    private static OrderLine NewLine(Ticket ticket, Guid lineId)
    {
        var line = ticket.Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new NotFoundException("That line is not on this ticket.");
        return line.Status switch
        {
            OrderLineStatus.New => line,
            OrderLineStatus.Printed => throw new DomainException($"{line.Name} is already printed. Printed items are changed with a void."),
            _ => throw new DomainException($"{line.Name} was already taken off."),
        };
    }

    private async Task Cancel(EditableTicket edit, OrderLine line, CancellationToken ct)
    {
        var grant = await permissions.Require(edit.User, PermissionKeys.OrderCancelUnprinted, approval.ApprovalToken, ct);
        line.Status = OrderLineStatus.Cancelled;
        line.CancelledAt = clock.GetUtcNow();
        line.CancelledByUserId = edit.User.UserId;
        audit.Write(new AuditRecord(AuditActions.LineCancelled, nameof(Ticket), edit.Ticket.Id.ToString(),
            new { ticket = edit.Ticket.Number, line = line.Name, line.Options, line.Quantity, amount = line.Gross }, null, grant.ApprovedByUserId));
    }

    private async Task<TicketDetailDto> Save(EditableTicket edit, CancellationToken ct)
    {
        edit.Ticket.SpreadDiscount();
        edit.Ticket.Touch(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await edit.Transaction.CommitAsync(ct);
        await events.Changed(edit.Ticket, edit.Ticket.TableId, ct);
        return await TicketDetails.Mine(db, edit.Ticket, edit.User, ct);
    }
}

/// <summary>The ticket note and, for takeaway, the customer's name.</summary>
public sealed class UpdateTicket(ITausiDb db, TicketEditing editing, IPermissionService permissions, TicketEvents events)
{
    public async Task<TicketDetailDto> Execute(Guid ticketId, UpdateTicketRequest request, CancellationToken ct)
    {
        await using var edit = await editing.Open(ticketId, ct);
        await permissions.Require(edit.User, PermissionKeys.OrderAdd, ct: ct);
        if (request.Note is not null)
        {
            var note = request.Note.Trim();
            if (note.Length > Ticket.MaxNoteLength) throw new DomainException($"A ticket note can be at most {Ticket.MaxNoteLength} characters.");
            edit.Ticket.Note = note;
        }
        if (request.CustomerName is not null)
        {
            var name = request.CustomerName.Trim();
            if (name.Length > Ticket.MaxCustomerNameLength)
                throw new DomainException($"The customer name can be at most {Ticket.MaxCustomerNameLength} characters.");
            edit.Ticket.CustomerName = name.Length == 0 ? null : name;
        }
        await db.SaveChangesAsync(ct);
        await edit.Transaction.CommitAsync(ct);
        await events.Changed(edit.Ticket, edit.Ticket.TableId, ct);
        return await TicketDetails.Mine(db, edit.Ticket, edit.User, ct);
    }
}
