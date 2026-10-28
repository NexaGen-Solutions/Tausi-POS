using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Menu;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;
using Tausi.Domain.People;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;

namespace Tausi.Tests.Tickets;

public class DiscountSpreadTests
{
    [Fact]
    public void Shares_follow_the_amounts_and_the_rounding_leftover_goes_on_the_largest()
    {
        DiscountSpread.Spread(100m, [100m, 100m, 100m]).Should().Equal(33.34m, 33.33m, 33.33m);
        DiscountSpread.Spread(10m, [70m, 250m, 30m]).Should().Equal(2.00m, 7.14m, 0.86m);
        DiscountSpread.Spread(0.05m, [1m, 1m, 1m]).Sum().Should().Be(0.05m);
    }

    [Theory]
    [InlineData(57.30, new[] { 123.45, 67.89, 0.01, 999.99 })]
    [InlineData(1, new[] { 0.33, 0.33, 0.34 })]
    [InlineData(500, new[] { 250.0, 250.0 })]
    public void Shares_add_up_exactly_and_never_exceed_a_line(double discount, double[] amounts)
    {
        var lines = amounts.Select(a => (decimal)a).ToList();
        var shares = DiscountSpread.Spread((decimal)discount, lines);
        shares.Sum().Should().Be(Math.Min((decimal)discount, lines.Sum()));
        shares.Zip(lines).Should().OnlyContain(p => p.First >= 0 && p.First <= p.Second);
    }
}

public class DiscountPriceTests : ApprovalsFixtureBase
{
    private async Task<Tausi.Application.Tickets.TicketDetailDto> MixedVat()
    {
        // 2 Tusker (16%, 500) and 1 exempt water (100).
        await F.Db.MenuItems.Where(i => i.Name == "House Water 1L")
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.VatExempt, true).SetProperty(i => i.VatRate, 0m));
        return await Printed("7", ("Tusker Lager", 2), ("House Water 1L", 1));
    }

    [Fact]
    public async Task A_percent_discount_is_spread_by_amount_and_vat_is_worked_out_after_it()
    {
        var ticket = await MixedVat();
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);
        Later();

        var after = await Discount(ticket.Id, DiscountKind.Percent, 10m, "Regular customer");

        after.Subtotal.Should().Be(600m);
        after.DiscountTotal.Should().Be(60m);
        after.Total.Should().Be(540m);
        var lines = F.Ticket(ticket.Number).Lines;
        var tusker = lines.Single(l => l.Name == "Tusker Lager");
        tusker.Should().Match<OrderLine>(l => l.Discount == 50m && l.Vat == 62.07m && l.Net == 387.93m);
        lines.Single(l => l.Name == "House Water 1L").Should().Match<OrderLine>(l => l.Discount == 10m && l.Vat == 0m && l.Net == 90m);
        lines.Sum(l => l.Discount).Should().Be(60m);

        var bill = Jobs(ticket.Id).Last(j => j.Kind == PrintJobKind.Bill).TextPreview;
        bill.Should().Contain("Corrected bill").And.MatchRegex(@"Subtotal +KSh 600")
            .And.MatchRegex(@"Discount 10% \(Regular customer\) +-KSh 60").And.MatchRegex(@"TOTAL +KSh 540")
            .And.Contain("Total includes VAT 16%: KSh 62.07").And.MatchRegex(@"VAT exempt: +KSh 90\.00");
        F.Read().DiscountLogs.Single().Should().Match<DiscountLog>(d => d.Kind == DiscountKind.Percent && d.Value == 10m && d.Amount == 60m);
    }

    [Fact]
    public async Task An_amount_discount_cant_exceed_the_total_and_percent_is_at_most_100()
    {
        var ticket = await MixedVat();
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);

        await FluentActions.Awaiting(() => Discount(ticket.Id, DiscountKind.Amount, 601m)).Should().ThrowAsync<DomainException>().WithMessage("*more than the total*");
        await FluentActions.Awaiting(() => Discount(ticket.Id, DiscountKind.Percent, 101m)).Should().ThrowAsync<DomainException>().WithMessage("*100%*");
        await FluentActions.Awaiting(() => Discount(ticket.Id, DiscountKind.Percent, 10m, "Other")).Should().ThrowAsync<DomainException>().WithMessage("*note*");

        var after = await Discount(ticket.Id, DiscountKind.Amount, 600m, "Compensation");
        after.Total.Should().Be(0m);
    }

    [Fact]
    public async Task The_discount_is_spread_again_when_a_line_is_added_or_voided_and_one_replaces_the_other()
    {
        var ticket = await MixedVat();
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);
        await Discount(ticket.Id, DiscountKind.Percent, 10m);

        var added = await F.Add(ticket.Id, "Pineapple Punch");
        added.DiscountTotal.Should().Be(87m);
        added.Total.Should().Be(783m);

        var voided = await Void(ticket.Id, LineOf(ticket, "Tusker Lager"), 2);
        voided.DiscountTotal.Should().Be(37m);
        voided.Total.Should().Be(333m);
        F.Ticket(ticket.Number).Lines.Single(l => l.Status == OrderLineStatus.Voided).Discount.Should().Be(0);

        var replaced = await Discount(ticket.Id, DiscountKind.Amount, 70m, "Promotion");
        replaced.DiscountTotal.Should().Be(70m);
        replaced.DiscountLabel.Should().Be("Discount (Promotion)");
        var removed = await Discount(ticket.Id, null, 0);
        removed.DiscountTotal.Should().Be(0m);
        removed.Total.Should().Be(370m);
        F.Read().DiscountLogs.Count().Should().Be(3);
    }

    [Fact]
    public async Task A_gifted_line_takes_no_discount()
    {
        var ticket = await MixedVat();
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);
        await Gift(ticket.Id, LineOf(ticket, "House Water 1L"), 1);

        var after = await Discount(ticket.Id, DiscountKind.Percent, 10m);

        after.DiscountTotal.Should().Be(50m);
        F.Ticket(ticket.Number).Lines.Single(l => l.IsGift).Discount.Should().Be(0);
    }

    [Fact]
    public async Task A_waiter_cant_discount_and_a_cashier_needs_approval()
    {
        var ticket = await MixedVat();
        await FluentActions.Awaiting(() => Discount(ticket.Id, DiscountKind.Percent, 10m)).Should().ThrowAsync<PermissionDeniedException>();
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        await FluentActions.Awaiting(() => Discount(ticket.Id, DiscountKind.Percent, 10m)).Should().ThrowAsync<ApprovalRequiredException>();
    }

    [Fact]
    public async Task A_price_change_keeps_the_original_price_and_is_logged_as_a_ticket_change()
    {
        var ticket = await Printed("7", ("Tusker Lager", 3));
        var admin = await F.LoginOn("Administrator", "Counter A");
        await TakeLock(ticket.Id);
        Later();

        var after = await ChangePrice(ticket.Id, LineOf(ticket, "Tusker Lager"), 200m, 2, "Happy hour");

        after.Total.Should().Be(650m);
        var changed = F.Ticket(ticket.Number).Lines.Single(l => l.OriginalUnitPrice != null);
        changed.Should().Match<OrderLine>(l => l.Quantity == 2 && l.UnitPrice == 200m && l.OriginalUnitPrice == 250m && l.Gross == 400m);
        F.Read().PriceChangeLogs.Single(p => p.Kind == PriceChangeKind.Ticket).Should().Match<PriceChangeLog>(p =>
            p.OldPrice == 250m && p.NewPrice == 200m && p.TicketId == ticket.Id && p.OrderLineId == changed.Id
            && p.ChangedByUserId == admin.UserId && p.ApprovedByUserId == admin.UserId && p.Reason == "Happy hour");
        Jobs(ticket.Id).Last(j => j.Kind == PrintJobKind.Bill).TextPreview.Should().Contain("2 x Tusker Lager (Cold)").And.Contain("400")
            .And.NotContain("250 ");

        // The menu is untouched, and its price history doesn't show ticket changes.
        F.Read().MenuItems.Include(i => i.Prices).Single(i => i.Name == "Tusker Lager").Prices.Single().Price.Should().Be(250m);
    }

    [Fact]
    public async Task Change_price_and_menu_edit_prices_are_separate_permissions()
    {
        var ticket = await Printed("7", ("Tusker Lager", 1));
        // A waiter: neither.
        await FluentActions.Awaiting(() => ChangePrice(ticket.Id, LineOf(ticket, "Tusker Lager"), 200m)).Should().ThrowAsync<PermissionDeniedException>();

        // A supervisor may edit menu prices but needs approval for a ticket price.
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);
        await FluentActions.Awaiting(() => ChangePrice(ticket.Id, LineOf(ticket, "Tusker Lager"), 200m)).Should().ThrowAsync<ApprovalRequiredException>();
        await F.SetPermission(DefaultPermissionRoles.Supervisor, PermissionKeys.OrderChangePrice, PermissionValue.NotAllowed);
        await FluentActions.Awaiting(() => ChangePrice(ticket.Id, LineOf(ticket, "Tusker Lager"), 200m)).Should().ThrowAsync<PermissionDeniedException>();
        await F.SetPermission(DefaultPermissionRoles.Supervisor, PermissionKeys.MenuEditPrices, PermissionValue.NotAllowed);
        await F.SetPermission(DefaultPermissionRoles.Supervisor, PermissionKeys.OrderChangePrice, PermissionValue.Allowed);
        (await ChangePrice(ticket.Id, LineOf(ticket, "Tusker Lager"), 200m)).Total.Should().Be(200m);
    }

    [Fact]
    public async Task Gift_discount_and_price_change_work_with_a_supervisors_pin()
    {
        var ticket = await Printed("7", ("Guiness", 2), ("Tusker Lager", 1));
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.OrderGift, "SAM");
        await Gift(ticket.Id, LineOf(ticket, "Guiness"));
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.OrderDiscount, "SAM");
        await Discount(ticket.Id, DiscountKind.Amount, 50m, "Promotion");

        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.OrderChangePrice, "Administrator");
        var after = await ChangePrice(ticket.Id, LineOf(ticket, "Tusker Lager"), 200m);

        after.Total.Should().Be(300m + 200m - 50m);
        var sam = F.User("SAM").Id;
        F.Read().GiftLogs.Single().Should().Match<GiftLog>(g => g.RequestedByUserId == F.User("CAROL").Id && g.ApprovedByUserId == sam);
        F.Read().DiscountLogs.Single().ApprovedByUserId.Should().Be(sam);
        F.Read().PriceChangeLogs.Single(p => p.Kind == PriceChangeKind.Ticket).Should().Match<PriceChangeLog>(p =>
            p.ChangedByUserId == sam && p.ApprovedByUserId == F.User("Administrator").Id);
    }

    [Fact]
    public async Task A_price_must_be_new_not_negative_and_have_a_reason()
    {
        var ticket = await Printed("7", ("Tusker Lager", 1));
        await F.LoginOn("Administrator", "Counter A");
        await TakeLock(ticket.Id);
        var line = LineOf(ticket, "Tusker Lager");

        await FluentActions.Awaiting(() => ChangePrice(ticket.Id, line, 250m)).Should().ThrowAsync<DomainException>().WithMessage("*already*");
        await FluentActions.Awaiting(() => ChangePrice(ticket.Id, line, -1m)).Should().ThrowAsync<DomainException>();
        await FluentActions.Awaiting(() => ChangePrice(ticket.Id, line, 200m, reason: " ")).Should().ThrowAsync<DomainException>().WithMessage("Give a reason.");
        (await ChangePrice(ticket.Id, line, 0m, reason: "Owner's guest")).Total.Should().Be(0m);
    }
}

internal static class DefaultPermissionRoles
{
    public const string Supervisor = "Supervisor";
}
