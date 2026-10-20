using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Infrastructure.Ledger;
using Tausi.Tests.Support;
using static Tausi.Domain.Ledger.JournalLineDraft;

namespace Tausi.Tests.Ledger;

public class LedgerPostingServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly LedgerPostingService _ledger;

    public LedgerPostingServiceTests()
    {
        foreach (var (code, name, type) in LedgerAccountCodes.Defaults)
            _db.Context.LedgerAccounts.Add(new LedgerAccount { Code = code, Name = name, Type = type });
        _db.Context.SaveChanges();
        _ledger = new LedgerPostingService(_db.Context, new FakeSession(), new FakeTimeProvider());
    }

    public void Dispose() => _db.Dispose();

    private static JournalEntryDraft Draft(params JournalLineDraft[] lines) =>
        new(DateTimeOffset.UtcNow, "Test", "Test", "1", lines);

    [Fact]
    public async Task Posts_a_balanced_cash_sale_with_vat()
    {
        // A 250 sale paid in cash: 215.52 sales + 34.48 VAT.
        await _ledger.Post(Draft(
            Dr(LedgerAccountCodes.Cash, 250m),
            Cr(LedgerAccountCodes.Sales, 215.52m),
            Cr(LedgerAccountCodes.VatPayable, 34.48m)));
        await _db.Context.SaveChangesAsync();

        var lines = await _db.NewContext().JournalLines.ToListAsync();
        lines.Should().HaveCount(3);
        lines.Sum(l => l.Debit).Should().Be(lines.Sum(l => l.Credit));
    }

    [Fact]
    public async Task Rejects_an_unbalanced_entry_and_writes_nothing()
    {
        var act = () => _ledger.Post(Draft(
            Dr(LedgerAccountCodes.Cash, 250m),
            Cr(LedgerAccountCodes.Sales, 215.52m)));

        await act.Should().ThrowAsync<UnbalancedEntryException>().WithMessage("*must equal*");
        await _db.Context.SaveChangesAsync();
        (await _db.NewContext().JournalEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Rejects_an_unknown_account()
    {
        var act = () => _ledger.Post(Draft(Dr("NOPE", 10m), Cr(LedgerAccountCodes.Sales, 10m)));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*NOPE*");
    }

    [Fact]
    public async Task Lines_carry_the_entry_source()
    {
        var entry = await _ledger.Post(new JournalEntryDraft(
            DateTimeOffset.UtcNow, "Expense", "Expense", "E-7",
            [Dr(LedgerAccountCodes.Expenses, 300m), Cr(LedgerAccountCodes.Cash, 300m)]));

        entry.Lines.Should().OnlyContain(l => l.SourceType == "Expense" && l.SourceId == "E-7");
    }
}

public class JournalEntryDraftTests
{
    private static JournalEntryDraft Draft(params JournalLineDraft[] lines) =>
        new(DateTimeOffset.UtcNow, "Test", "Test", null, lines);

    [Fact]
    public void Balanced_entry_passes()
    {
        var act = () => Draft(Dr("CASH", 100m), Cr("SALES", 86.21m), Cr("VAT_PAYABLE", 13.79m)).EnsureBalanced();
        act.Should().NotThrow();
    }

    [Fact]
    public void Off_by_one_cent_is_rejected()
    {
        var act = () => Draft(Dr("CASH", 100m), Cr("SALES", 86.21m), Cr("VAT_PAYABLE", 13.78m)).EnsureBalanced();
        act.Should().Throw<UnbalancedEntryException>();
    }

    [Fact]
    public void Single_line_is_rejected()
    {
        var act = () => Draft(Dr("CASH", 0.01m)).EnsureBalanced();
        act.Should().Throw<UnbalancedEntryException>();
    }

    [Fact]
    public void Negative_amounts_are_rejected()
    {
        var act = () => Draft(Dr("CASH", -5m), Cr("SALES", -5m)).EnsureBalanced();
        act.Should().Throw<UnbalancedEntryException>();
    }

    [Fact]
    public void A_line_with_both_debit_and_credit_is_rejected()
    {
        var act = () => Draft(new JournalLineDraft("CASH", 5m, 5m), Dr("SALES", 1m), Cr("BANK", 1m)).EnsureBalanced();
        act.Should().Throw<UnbalancedEntryException>();
    }

    [Fact]
    public void Fractions_of_a_cent_are_rejected()
    {
        var act = () => Draft(Dr("CASH", 1.005m), Cr("SALES", 1.005m)).EnsureBalanced();
        act.Should().Throw<UnbalancedEntryException>();
    }
}
