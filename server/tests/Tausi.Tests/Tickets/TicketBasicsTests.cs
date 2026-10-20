using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.Service;

namespace Tausi.Tests.Tickets;

public class TicketBasicsTests : IAsyncLifetime
{
    private TicketsFixture _f = null!;
    public async Task InitializeAsync() => _f = await TicketsFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    [Fact]
    public async Task A_saved_ticket_gets_the_next_number_and_records_who_and_where()
    {
        var first = await _f.NewTicket("4", ("Tusker Lager", 2));
        var second = await _f.NewTicket("5");

        first.Number.Should().Be(1001);
        second.Number.Should().Be(1002);
        var saved = _f.Ticket(first.Number);
        saved.OpenedByUserId.Should().Be(_f.User("edna").Id);
        saved.Lines.Should().ContainSingle().Which.AddedByUserId.Should().Be(_f.User("edna").Id);
        saved.Lines[0].AddedTerminalId.Should().Be(_f.Read().Terminals.Single(t => t.Name == "Counter A").Id);
        saved.Lines[0].Gross.Should().Be(500m);
        saved.Lines[0].Vat.Should().Be(68.97m, "VAT = 500 x 16 / 116, rounded half away from zero");
        _f.Events.Published.Should().Contain(e => e.Name == PosEventNames.TicketChanged);
    }

    [Fact]
    public async Task The_total_is_the_sum_of_the_lines_without_voided_ones()
    {
        var created = await _f.NewTicket("4", ("Tusker Lager", 2), ("Guiness", 1));
        created.Total.Should().Be(800m);

        var line = await _f.Db.OrderLines.FirstAsync(l => l.Name == "Guiness");
        line.VoidedAt = _f.Clock.GetUtcNow();
        await _f.Db.SaveChangesAsync();

        _f.Ticket(created.Number).Total.Should().Be(500m);
        (await _f.Get<GetTicket>().Execute(created.Id, CancellationToken.None)).Total.Should().Be(500m);
        var map = await _f.Get<GetTableMap>().Execute(null, CancellationToken.None);
        map.Tables.Single(t => t.Name == "4").Tickets.Single().Total.Should().Be(500m);
    }

    [Fact]
    public async Task No_ticket_can_be_saved_while_no_day_is_open()
    {
        await _f.Db.WorkPeriods.ExecuteUpdateAsync(u => u.SetProperty(w => w.IsOpen, false));

        var act = () => _f.NewTicket("4");

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(TicketCodes.DayClosed);
        _f.Read().Tickets.Should().BeEmpty();
        (await _f.Get<GetTableMap>().Execute(null, CancellationToken.None)).DayOpen.Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_ticket_is_never_saved()
    {
        var act = () => _f.Get<StartTicket>().Execute(new StartTicketRequest(_f.Table("4").Id, null, []), CancellationToken.None);

        await act.Should().ThrowAsync<DomainException>().WithMessage("A ticket needs at least one line.");
    }

    [Fact]
    public async Task Tickets_go_on_active_tables_only()
    {
        await _f.Db.Tables.Where(t => t.Name == "16").ExecuteUpdateAsync(u => u.SetProperty(t => t.Active, false));

        var act = () => _f.NewTicket("16");

        await act.Should().ThrowAsync<DomainException>().WithMessage("That table is not in use.");
    }

    [Fact]
    public async Task Any_waiter_can_open_another_waiters_ticket()
    {
        var created = await _f.NewTicket("4");
        await _f.Get<UnlockTicket>().Execute(created.Id, CancellationToken.None);
        await _f.LoginOn("Ruth", "Counter B");

        (await _f.Get<LockTicket>().Execute(created.Id, CancellationToken.None)).Editable.Should().BeTrue();
    }

    [Fact]
    public void Merging_moves_lines_unchanged_and_joins_notes()
    {
        var table = Guid.NewGuid();
        var into = new Ticket { Number = 1, TableId = table, Note = "Birthday", OpenedAt = DateTimeOffset.UnixEpoch };
        var other = new Ticket { Number = 2, TableId = table, Note = "No ice" };
        var line = new OrderLine { TicketId = other.Id, Name = "Tusker", Gross = 250m, AddedByUserId = Guid.NewGuid(), AddedTerminalId = Guid.NewGuid() };
        other.Lines.Add(line);
        var addedBy = line.AddedByUserId;

        into.MergeIn([other], DateTimeOffset.UnixEpoch.AddHours(1));

        into.Lines.Should().ContainSingle().Which.Should().BeSameAs(line);
        line.TicketId.Should().Be(into.Id);
        line.AddedByUserId.Should().Be(addedBy);
        into.Note.Should().Be("Birthday / No ice");
        other.Status.Should().Be(TicketStatus.Merged);
        other.MergedIntoTicketId.Should().Be(into.Id);
        into.LastActivityAt.Should().Be(DateTimeOffset.UnixEpoch.AddHours(1));
    }
}
