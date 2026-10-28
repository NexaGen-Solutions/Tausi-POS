using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Printing;
using Tausi.Application.Reports;
using Tausi.Application.Settings;
using Tausi.Application.Settling;
using Tausi.Application.Tickets;
using Tausi.Domain.Ledger;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;
using Tausi.Tests.Stock;

namespace Tausi.Tests.Settings;

/// <summary>
/// Task 16, Part D: a training terminal's sales never reach reports, the ledger, stock or cash-ups; its
/// slips say TRAINING; Clear training data deletes them and is audited.
/// </summary>
public class TrainingModeTests : StockTestBase
{
    private sealed record Real(int Tickets, int Payments, int Entries, int Movements, int Shifts, decimal TuskerInBar);

    private Real Snapshot()
    {
        var db = F.Read();
        return new Real(db.Tickets.Count(), db.Payments.Count(), db.JournalEntries.Count(), db.StockMovements.Count(),
            db.CashierShifts.Count(), OnHand("Tusker Lager", "Bar"));
    }

    /// <summary>Counter A in training, with its training day open (as PIN login on it does).</summary>
    private async Task CounterAInTraining()
    {
        var counterA = F.Db.Terminals.Single(t => t.Name == "Counter A");
        counterA.TrainingMode = true;
        await F.Db.SaveChangesAsync();
        F.Db.TrainingMode = true;
        F.Db.WorkPeriods.Add(new WorkPeriod { OpenedAt = F.Clock.GetUtcNow(), OpenedByUserId = F.User("SAM").Id, OpenedTerminalId = counterA.Id });
        await F.Db.SaveChangesAsync();
    }

    /// <summary>A training sale on Counter A: printed, paid in cash and settled by CAROL on a training shift.</summary>
    private async Task<TicketDetailDto> TrainingSale()
    {
        await CounterAInTraining();
        var ticket = await Printed("7", ("Tusker Lager", 2));
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        await StartShift(1000m);
        Later();
        await F.Get<PaymentCommands>().Add(ticket.Id, new AddPaymentRequest(PaymentType.Cash, null, 500m, null, null, null), None);
        await F.Get<SettleTicket>().Execute(ticket.Id, None);
        return ticket;
    }

    [Fact]
    public async Task A_training_sale_never_reaches_reports_the_ledger_stock_or_cash_ups()
    {
        var before = Snapshot();
        var ticket = await TrainingSale();

        var after = Snapshot();
        after.Should().Be(before, "real tickets, payments, journal entries, stock movements, shifts and stock on hand are untouched");
        F.Read().Tickets.IgnoreQueryFilters().Single(t => t.Id == ticket.Id)
            .Should().Match<Ticket>(t => t.IsTraining && t.Status == TicketStatus.Settled);
        F.Read().Payments.IgnoreQueryFilters().Where(p => p.TicketId == ticket.Id).Should().OnlyContain(p => p.IsTraining);
        F.Read().CashierShifts.IgnoreQueryFilters().Should().ContainSingle(s => s.IsTraining);

        // The admin site's reports (a real request) don't see it.
        F.Db.TrainingMode = false;
        await AsAdmin();
        var sales = await F.Get<ReportService>().Run("sales-by-user", new ReportFilters { IncludeOpen = true }, None);
        sales.Tables.SelectMany(t => t.Groups).SelectMany(g => g.Rows).Should().NotContain(r => r.TicketId == ticket.Id);
        var payments = await F.Get<ReportService>().Run("payments", new ReportFilters(), None);
        payments.Tables.SelectMany(t => t.Groups).SelectMany(g => g.Rows).Should().NotContain(r => r.TicketId == ticket.Id);
    }

    [Fact]
    public async Task Every_slip_from_a_training_terminal_says_TRAINING_at_the_top_and_the_bottom()
    {
        var ticket = await TrainingSale();

        var jobs = F.Read().PrintJobs.Where(j => j.TicketId == ticket.Id).ToList();
        jobs.Select(j => j.Kind).Should().Contain([PrintJobKind.Bill, PrintJobKind.Dispense, PrintJobKind.Receipt]);
        foreach (var job in jobs)
        {
            var lines = job.TextPreview.TrimEnd().Split('\n');
            lines[0].Trim().Should().Be(ReceiptBuilder.TrainingBanner);
            lines.Count(l => l.Trim() == ReceiptBuilder.TrainingBanner).Should().Be(2, $"the {job.Kind} has it at the top and the bottom");
            System.Text.Encoding.ASCII.GetString(job.Content).Split(ReceiptBuilder.TrainingBanner).Length.Should().Be(3);
        }
    }

    [Fact]
    public async Task Training_tickets_and_receipts_count_on_their_own_so_real_numbers_keep_no_gaps()
    {
        var realBefore = F.Read().Tickets.Max(t => (long?)t.Number) ?? Tausi.Infrastructure.Persistence.TausiDbContext.FirstTicketNumber - 1;
        var ticket = await TrainingSale();

        var training = F.Read().Tickets.IgnoreQueryFilters().Single(t => t.Id == ticket.Id);
        training.Number.Should().Be(Tausi.Infrastructure.Persistence.TausiDbContext.FirstTicketNumber);
        training.ReceiptNumber.Should().Be(Tausi.Infrastructure.Persistence.TausiDbContext.FirstReceiptNumber);

        F.Db.TrainingMode = false;
        var real = await F.NewTicket("8", ("Tusker Lager", 1));
        real.Number.Should().Be(realBefore + 1);
    }

    [Fact]
    public async Task A_training_terminal_cant_change_real_data_whatever_the_code_does()
    {
        await CounterAInTraining();
        var item = F.Db.StockItems.First();
        var cost = item.AverageCost;
        item.AverageCost = cost + 99;
        F.Db.JournalEntries.Add(new JournalEntry { Date = F.Clock.GetUtcNow(), Memo = "from training", SourceType = "Test", CreatedAt = F.Clock.GetUtcNow() });
        await F.Db.SaveChangesAsync();

        F.Read().StockItems.Single(i => i.Id == item.Id).AverageCost.Should().Be(cost);
        F.Read().JournalEntries.Should().NotContain(e => e.Memo == "from training");
    }

    [Fact]
    public async Task Clear_training_data_deletes_it_all_keeps_real_sales_and_is_audited()
    {
        F.Db.TrainingMode = false;
        var real = await Printed("8", ("Guiness", 1));
        var ticket = await TrainingSale();

        F.Db.TrainingMode = false;
        await AsAdmin();
        (await F.Get<TrainingAdmin>().Status(None)).Tickets.Should().Be(1);
        (await F.Get<TrainingAdmin>().Clear(None)).Tickets.Should().Be(1);

        var db = F.Read();
        db.Tickets.IgnoreQueryFilters().Should().NotContain(t => t.IsTraining);
        db.Payments.IgnoreQueryFilters().Should().NotContain(p => p.IsTraining);
        db.OrderLines.IgnoreQueryFilters().Should().NotContain(l => l.IsTraining);
        db.CashierShifts.IgnoreQueryFilters().Should().NotContain(s => s.IsTraining);
        db.WorkPeriods.IgnoreQueryFilters().Should().NotContain(w => w.IsTraining);
        db.PrintJobs.Should().NotContain(j => j.TicketId == ticket.Id);
        db.Tickets.Should().Contain(t => t.Id == real.Id, "real sales stay");
        db.AuditEntries.Should().ContainSingle(a => a.Action == AuditActions.TrainingDataCleared);
    }

    [Fact]
    public async Task Switching_training_on_needs_settings_manage_is_audited_and_tells_the_terminals()
    {
        var counterA = F.Read().Terminals.Single(t => t.Name == "Counter A");
        await F.LoginOn("edna", "Counter A");
        await FluentActions.Awaiting(() => F.Get<TrainingAdmin>().SetTerminal(counterA.Id, new SetTrainingRequest(true, counterA.RowVersion), None))
            .Should().ThrowAsync<Tausi.Application.Security.PermissionDeniedException>();

        await AsAdmin();
        var on = await F.Get<TrainingAdmin>().SetTerminal(counterA.Id, new SetTrainingRequest(true, counterA.RowVersion), None);
        on.Training.Should().BeTrue();
        F.Read().AuditEntries.Should().ContainSingle(a => a.Action == AuditActions.TrainingModeChanged && a.EntityId == counterA.Id.ToString());
        F.Events.Published.Select(e => e.Name).Should().Contain(["settings.changed", "tables.changed"]);
        await FluentActions.Awaiting(() => F.Get<TrainingAdmin>().SetTerminal(counterA.Id, new SetTrainingRequest(false, counterA.RowVersion), None))
            .Should().ThrowAsync<ConflictException>("a stale version is refused");
    }

    [Fact]
    public async Task Signing_in_on_a_training_terminal_opens_its_training_day_and_leaves_the_real_day_alone()
    {
        var edna = F.Db.Users.Single(u => u.Name == "edna");
        edna.PinHash = F.Hasher.Hash("8642");
        edna.PinLookup = F.Hasher.Lookup("8642");
        var counterA = F.Db.Terminals.Single(t => t.Name == "Counter A");
        counterA.TrainingMode = true;
        await F.Db.SaveChangesAsync();
        F.Db.TrainingMode = true;

        await F.Get<Tausi.Application.Auth.PinAuthenticator>().Authenticate(counterA.Id, "8642", None);
        await F.Get<Tausi.Application.Auth.PinAuthenticator>().Authenticate(counterA.Id, "8642", None);

        var days = F.Read().WorkPeriods.IgnoreQueryFilters().Where(w => w.IsOpen).ToList();
        days.Should().HaveCount(2).And.ContainSingle(w => w.IsTraining && w.OpenedTerminalId == counterA.Id, "one training day, opened once");
        days.Should().ContainSingle(w => !w.IsTraining, "the real day is still the only real one");
    }
}
