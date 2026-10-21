using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Domain.Service;

namespace Tausi.Tests.Tickets;

/// <summary>A day closes only when no ticket is open (the owner's choice, as in SambaPOS).</summary>
public class DayClosingTests : IAsyncLifetime
{
    private TicketsFixture _f = null!;
    public async Task InitializeAsync() => _f = await TicketsFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task Check() => DayClosing.EnsureNoOpenTickets(_f.Read(), CancellationToken.None);

    [Fact]
    public async Task The_day_can_close_when_no_ticket_is_open()
    {
        await FluentActions.Awaiting(Check).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Open_tickets_keep_the_day_open_and_are_listed()
    {
        var first = await _f.NewTicket("4");
        var second = await _f.NewTicket("7");

        var error = (await FluentActions.Awaiting(Check).Should().ThrowAsync<ConflictException>()).Which;

        error.Code.Should().Be(DayClosing.OpenTicketsCode);
        error.Message.Should().Be($"The day can't close while 2 tickets are open (#{first.Number}, #{second.Number}). Settle or move them first.");
    }

    [Fact]
    public async Task Settled_merged_and_cancelled_tickets_do_not_keep_it_open()
    {
        var ticket = await _f.NewTicket("4");
        await _f.Db.Tickets.Where(k => k.Id == ticket.Id).ExecuteUpdateAsync(u => u.SetProperty(k => k.Status, TicketStatus.Settled));

        await FluentActions.Awaiting(Check).Should().NotThrowAsync();
    }
}
