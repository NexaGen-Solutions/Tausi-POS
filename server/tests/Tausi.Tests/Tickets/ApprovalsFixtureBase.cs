using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Approvals;
using Tausi.Application.Tickets;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;

namespace Tausi.Tests.Tickets;

/// <summary>Helpers for voids, gifts, discounts, price changes and approval requests.</summary>
public abstract class ApprovalsFixtureBase : IAsyncLifetime
{
    protected TicketsFixture F = null!;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task InitializeAsync() => F = await TicketsFixture.Create();
    public async Task DisposeAsync() => await F.DisposeAsync();

    protected static readonly CancellationToken None = CancellationToken.None;

    /// <summary>edna on Counter A: a ticket on <paramref name="table"/> with these lines, printed (bill 1 and a dispense slip).</summary>
    protected async Task<TicketDetailDto> Printed(string table, params (string Item, decimal Qty)[] lines)
    {
        var ticket = await F.NewTicket(table, lines);
        return await F.Get<PrintTicket>().Print(ticket.Id, None);
    }

    protected Guid LineOf(TicketDetailDto ticket, string name, string status = "Printed") =>
        ticket.Lines.First(l => l.Name == name && l.Status == status && !l.IsGift).Id;

    protected Task<TicketDetailDto> Void(Guid ticketId, Guid lineId, decimal qty = 1, string reason = "Wrong item", string? note = null, bool? poured = null) =>
        F.Get<TicketActionCommands>().Void(ticketId, lineId, new VoidLineRequest(qty, reason, note, poured), None);

    protected Task<TicketDetailDto> Gift(Guid ticketId, Guid lineId, decimal qty = 1, string reason = "Regular customer") =>
        F.Get<TicketActionCommands>().Gift(ticketId, lineId, new GiftLineRequest(qty, reason), None);

    protected Task<TicketDetailDto> Discount(Guid ticketId, DiscountKind? kind, decimal value, string reason = "Regular customer") =>
        F.Get<TicketActionCommands>().Discount(ticketId, new DiscountRequest(kind, value, reason), None);

    protected Task<TicketDetailDto> ChangePrice(Guid ticketId, Guid lineId, decimal price, decimal qty = 1, string reason = "Happy hour") =>
        F.Get<TicketActionCommands>().ChangePrice(ticketId, lineId, new ChangePriceRequest(qty, price, reason), None);

    protected Task<ApprovalRequestDto> Request(ApprovalKind kind, Guid ticketId, Guid? lineId, object payload, Guid? preferred = null) =>
        F.Get<CreateApprovalRequest>().Execute(
            new CreateApprovalRequestDto(kind, ticketId, lineId, JsonSerializer.SerializeToElement(payload, Json), preferred), None);

    protected Task<ApprovalRequestDto> Approve(Guid id) => F.Get<ApproveRequest>().Execute(id, None);
    protected Task<ApprovalRequestDto> Reject(Guid id, string? note = null) => F.Get<RejectRequest>().Execute(id, new RejectApprovalRequestDto(note), None);
    protected Task<ApprovalRequestDto> Cancel(Guid id) => F.Get<CancelRequest>().Execute(id, None);

    protected Task<TicketDetailDto> Detail(Guid id) => F.Get<GetTicket>().Execute(id, None);

    /// <summary>
    /// The current user takes the ticket's edit lock (opens it on their terminal), after the
    /// terminal that had it open stopped renewing it.
    /// </summary>
    protected async Task TakeLock(Guid ticketId)
    {
        F.Clock.Advance(Ticket.LockLifetime + TimeSpan.FromSeconds(1));
        var state = await F.Get<LockTicket>().Execute(ticketId, None);
        if (!state.Editable) throw new InvalidOperationException(state.Message);
    }

    protected List<PrintJob> Jobs(Guid ticketId) =>
        F.Read().PrintJobs.Include(j => j.Printer).Where(j => j.TicketId == ticketId).AsEnumerable()
            .OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).ToList();

    /// <summary>The current user starts their cashier shift on their terminal (settling needs one).</summary>
    protected Task<Tausi.Application.Cash.ShiftDto> StartShift(decimal openingFloat = 0m) =>
        F.Get<Tausi.Application.Cash.OpenShift>().Execute(
            new Tausi.Application.Cash.OpenShiftRequest(new Tausi.Application.Cash.CashCount(Total: openingFloat)), None);

    /// <summary>Lets the fake clock move on, so print jobs of later rounds sort after earlier ones.</summary>
    protected void Later() => F.Clock.Advance(TimeSpan.FromSeconds(2));
}
