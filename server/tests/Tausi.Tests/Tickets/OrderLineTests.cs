using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Tests.Tickets;

public class OrderLineTests : IAsyncLifetime
{
    private TicketsFixture _f = null!;
    public async Task InitializeAsync() => _f = await TicketsFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private OrderLine LineOf(Guid ticketId, string name, string? options = null) =>
        _f.Read().OrderLines.Single(l => l.TicketId == ticketId && l.Name == name && l.Options == options && l.Status != OrderLineStatus.Cancelled);

    [Fact]
    public async Task The_first_line_saves_the_ticket_with_a_number_and_copies_name_price_and_vat()
    {
        var ticket = await _f.Start("4", "Tusker Lager", 2, "Cold");

        ticket.Number.Should().Be(1001);
        var line = LineOf(ticket.Id, "Tusker Lager", "Cold");
        line.Should().Match<OrderLine>(l => l.ShortName == "Tusker Lager" && l.UnitPrice == 250m && l.Quantity == 2
            && l.Gross == 500m && l.Vat == 68.97m && l.Net == 431.03m && l.VatRate == 16m && l.Status == OrderLineStatus.New);
        line.OptionIds.Should().Be(_f.Option("Cold").ToString());
        line.AddedByUserId.Should().Be(_f.User("edna").Id);
        line.AddedTerminalId.Should().Be(_f.Session.User!.TerminalId!.Value);
    }

    [Fact]
    public async Task The_price_comes_from_the_terminals_price_list_and_stays_when_the_menu_changes()
    {
        var happyHour = new PriceList { Name = "Happy hour" };
        _f.Db.PriceLists.Add(happyHour);
        var tusker = await _f.Db.MenuItems.SingleAsync(i => i.Name == "Tusker Lager");
        _f.Db.MenuItemPrices.Add(new MenuItemPrice { MenuItemId = tusker.Id, PriceListId = happyHour.Id, Price = 200m });
        var counterA = await _f.Db.Terminals.SingleAsync(t => t.Name == "Counter A");
        counterA.PriceListId = happyHour.Id;
        await _f.Db.SaveChangesAsync();

        var ticket = await _f.Start("4", "Tusker Lager", 1, "Cold");
        await _f.Add(ticket.Id, "Pineapple Punch");
        LineOf(ticket.Id, "Tusker Lager", "Cold").UnitPrice.Should().Be(200m);
        LineOf(ticket.Id, "Pineapple Punch").UnitPrice.Should().Be(270m, "not on Happy hour, so the default list's price");

        await _f.Db.MenuItemPrices.Where(p => p.PriceListId == happyHour.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Price, 999m));
        LineOf(ticket.Id, "Tusker Lager", "Cold").UnitPrice.Should().Be(200m, "a line keeps the price it was added at");
    }

    [Fact]
    public async Task An_item_with_no_price_is_refused()
    {
        var category = await _f.Db.Categories.SingleAsync(c => c.Name == "Food");
        _f.Db.MenuItems.Add(new MenuItem { Name = "Chips", ShortName = "Chips", CategoryId = category.Id });
        await _f.Db.SaveChangesAsync();
        var ticket = await _f.Start("4", "Pineapple Punch");

        var act = () => _f.Add(ticket.Id, "Chips");

        await act.Should().ThrowAsync<DomainException>().WithMessage("Chips has no price on this terminal's price list.");
    }

    [Fact]
    public async Task The_same_item_again_adds_to_my_own_new_line_only()
    {
        var ticket = await _f.Start("4", "Tusker Lager", 1, "Cold");
        await _f.Add(ticket.Id, "Tusker Lager", 2, ["Cold"]);
        LineOf(ticket.Id, "Tusker Lager", "Cold").Quantity.Should().Be(3, "same item, options, price and person: one line");

        await _f.Add(ticket.Id, "Tusker Lager", 1, ["Warm"]);
        _f.Read().OrderLines.Count(l => l.TicketId == ticket.Id).Should().Be(2, "different options make a new line");

        await _f.Add(ticket.Id, "Tusker Lager", 1, ["Cold"], note: "In a glass");
        _f.Read().OrderLines.Count(l => l.TicketId == ticket.Id).Should().Be(3, "a line with a note is its own line");

        // Someone else on the same terminal (after edna logs out) gets their own line.
        await _f.Get<UnlockTicket>().Execute(ticket.Id, CancellationToken.None);
        await _f.LoginOn("Ruth", "Counter A");
        await _f.Get<LockTicket>().Execute(ticket.Id, CancellationToken.None);
        await _f.Add(ticket.Id, "Tusker Lager", 1, ["Cold"]);
        _f.Read().OrderLines.Count(l => l.TicketId == ticket.Id && l.Options == "Cold" && l.Note == null).Should().Be(2);
    }

    [Fact]
    public async Task A_printed_line_or_a_different_price_is_never_added_to()
    {
        var ticket = await _f.Start("4", "Tusker Lager", 1, "Cold");
        await _f.Get<PrintTicket>().Print(ticket.Id, CancellationToken.None);
        await _f.Add(ticket.Id, "Tusker Lager", 1, ["Cold"]);
        _f.Read().OrderLines.Where(l => l.TicketId == ticket.Id).AsEnumerable().Select(l => (l.Status, l.Quantity)).Should()
            .BeEquivalentTo([(OrderLineStatus.Printed, 1m), (OrderLineStatus.New, 1m)]);

        await _f.Db.MenuItemPrices.Where(p => p.MenuItemId == _f.Item("Tusker Lager")).ExecuteUpdateAsync(u => u.SetProperty(p => p.Price, 260m));
        await _f.Add(ticket.Id, "Tusker Lager", 1, ["Cold"]);
        _f.Read().OrderLines.Count(l => l.TicketId == ticket.Id && l.Status == OrderLineStatus.New).Should().Be(2, "the new price starts its own line");
    }

    [Fact]
    public async Task A_required_choice_can_not_be_skipped_and_too_many_are_refused()
    {
        var ticket = await _f.Start("4", "Pineapple Punch");

        await FluentActions.Awaiting(() => _f.Add(ticket.Id, "Tusker Lager")).Should().ThrowAsync<DomainException>()
            .WithMessage("Tusker Lager: Temperature · choose 1.");
        await FluentActions.Awaiting(() => _f.Add(ticket.Id, "Tusker Lager", 1, ["Cold", "Warm"])).Should().ThrowAsync<DomainException>()
            .WithMessage("Tusker Lager: Temperature · choose 1.");
        await FluentActions.Awaiting(() => _f.Add(ticket.Id, "Pineapple Punch", 1, ["Cold"])).Should().ThrowAsync<DomainException>()
            .WithMessage("That choice isn't available for Pineapple Punch.");
        await FluentActions.Awaiting(() => _f.Start("5", "Guiness")).Should().ThrowAsync<DomainException>();
        _f.Read().Tickets.Count().Should().Be(1, "a refused first line saves no ticket");
    }

    [Fact]
    public async Task New_lines_change_quantity_and_cancelling_needs_permission_and_is_kept()
    {
        var ticket = await _f.Start("4", "Tusker Lager", 2, "Cold");
        var line = LineOf(ticket.Id, "Tusker Lager", "Cold");
        var change = _f.Get<ChangeLine>();

        (await change.Quantity(ticket.Id, line.Id, new ChangeLineRequest(3), CancellationToken.None)).Total.Should().Be(750m);
        LineOf(ticket.Id, "Tusker Lager", "Cold").Vat.Should().Be(103.45m);

        await _f.SetPermission("Waiter", PermissionKeys.OrderCancelUnprinted, PermissionValue.NotAllowed);
        await FluentActions.Awaiting(() => _f.Get<ChangeLine>().Quantity(ticket.Id, line.Id, new ChangeLineRequest(0), CancellationToken.None))
            .Should().ThrowAsync<PermissionDeniedException>();
        await FluentActions.Awaiting(() => _f.Get<ChangeLine>().Remove(ticket.Id, line.Id, CancellationToken.None))
            .Should().ThrowAsync<PermissionDeniedException>();

        await _f.SetPermission("Waiter", PermissionKeys.OrderCancelUnprinted, PermissionValue.Allowed);
        var after = await _f.Get<ChangeLine>().Quantity(ticket.Id, line.Id, new ChangeLineRequest(0), CancellationToken.None);
        after.Lines.Should().BeEmpty("a cancelled line is not shown");
        after.Total.Should().Be(0m);
        var kept = _f.Read().OrderLines.Single(l => l.Id == line.Id);
        kept.Status.Should().Be(OrderLineStatus.Cancelled);
        kept.CancelledByUserId.Should().Be(_f.User("edna").Id);
        _f.Read().AuditEntries.Should().Contain(a => a.Action == AuditActions.LineCancelled);
    }

    [Fact]
    public async Task Printed_lines_can_not_be_changed_or_removed_here()
    {
        var ticket = await _f.Start("4", "Pineapple Punch", 2);
        await _f.Get<PrintTicket>().Print(ticket.Id, CancellationToken.None);
        var line = LineOf(ticket.Id, "Pineapple Punch");

        await FluentActions.Awaiting(() => _f.Get<ChangeLine>().Quantity(ticket.Id, line.Id, new ChangeLineRequest(1), CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage("*already printed*");
        await FluentActions.Awaiting(() => _f.Get<ChangeLine>().Remove(ticket.Id, line.Id, CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage("*already printed*");
        LineOf(ticket.Id, "Pineapple Punch").Quantity.Should().Be(2);
    }

    [Fact]
    public async Task Writes_need_this_terminals_edit_lock()
    {
        var ticket = await _f.Start("4", "Pineapple Punch");
        await _f.LoginOn("Ruth", "Pillar 1");

        var elsewhere = await FluentActions.Awaiting(() => _f.Add(ticket.Id, "Sodas 300ml")).Should().ThrowAsync<ConflictException>();
        elsewhere.Which.Code.Should().Be(TicketCodes.Locked);
        elsewhere.Which.Message.Should().Be("Open on Counter A by edna. You can view it but not change it.");

        _f.Clock.Advance(TimeSpan.FromSeconds(91)); // edna's lock runs out; Ruth hasn't taken it
        (await FluentActions.Awaiting(() => _f.Get<PrintTicket>().Print(ticket.Id, CancellationToken.None)).Should().ThrowAsync<ConflictException>())
            .Which.Code.Should().Be(TicketEditing.NotLockedCode);
        _f.Read().OrderLines.Count(l => l.TicketId == ticket.Id).Should().Be(1);
    }

    [Fact]
    public async Task The_ticket_note_and_customer_name_can_change()
    {
        var ticket = await _f.Get<StartTicket>().Execute(
            new StartTicketRequest(null, "Otieno", [new NewLineRequest(_f.Item("Pineapple Punch"), 1)]), CancellationToken.None);

        var updated = await _f.Get<UpdateTicket>().Execute(ticket.Id, new UpdateTicketRequest("No straws", "Otieno W."), CancellationToken.None);

        updated.Note.Should().Be("No straws");
        updated.CustomerName.Should().Be("Otieno W.");
    }
}
