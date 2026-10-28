using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Settling;
using Tausi.Application.Support;
using Tausi.Domain.Service;
using Tausi.Tests.Stock;

namespace Tausi.Tests.Support;

/// <summary>Task 16: the check after a power cut or a load test finds what doesn't agree, and nothing else.</summary>
public class ConsistencyCheckTests : StockTestBase
{
    private async Task<Guid> SettledSale()
    {
        var ticket = await Printed("7", ("Tusker Lager", 2));
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        await StartShift(1000m);
        Later();
        await F.Get<PaymentCommands>().Add(ticket.Id, new AddPaymentRequest(PaymentType.Cash, null, 500m, null, null, null), None);
        await F.Get<SettleTicket>().Execute(ticket.Id, None);
        return ticket.Id;
    }

    private Task<ConsistencyReport> Check() => new ConsistencyCheck(F.Read(), F.Clock).Run(None);

    [Fact]
    public async Task Real_service_agrees_with_itself()
    {
        await SettledSale();
        var report = await Check();
        report.Problems.Should().BeEmpty();
        report.SettledTickets.Should().Be(1);
        report.TotalDebit.Should().Be(report.TotalCredit).And.BePositive();
    }

    [Fact]
    public async Task A_lost_sale_entry_a_short_payment_an_unbalanced_entry_and_a_wrong_stock_cache_are_all_found()
    {
        var ticket = await SettledSale();
        var db = F.Read();
        var sale = db.JournalEntries.Single(e => e.SourceType == SalePosting.SourceType && e.SourceId == ticket.ToString());
        await db.JournalLines.Where(l => l.JournalEntryId == sale.Id).ExecuteDeleteAsync();
        await db.JournalEntries.Where(e => e.Id == sale.Id).ExecuteDeleteAsync();
        await db.Payments.Where(p => p.TicketId == ticket).ExecuteUpdateAsync(u => u.SetProperty(p => p.Amount, 1m));
        var other = db.JournalLines.First();
        await db.JournalLines.Where(l => l.Id == other.Id).ExecuteUpdateAsync(u => u.SetProperty(l => l.Debit, l => l.Debit + 7m));
        await db.StockLevels.Take(1).ExecuteUpdateAsync(u => u.SetProperty(l => l.OnHand, l => l.OnHand + 3m));

        var report = await Check();
        report.Ok.Should().BeFalse();
        report.Problems.Select(p => p.Area).Distinct().Should().BeEquivalentTo(["ledger", "tickets", "stock"]);
        report.Problems.Should().Contain(p => p.Detail.Contains("has 0 sale journal entries"));
        report.Problems.Should().Contain(p => p.Detail.Contains("payments come to 1.00"));
        report.Problems.Should().Contain(p => p.Detail.Contains("trial balance is off by 7.00"));
    }
}
