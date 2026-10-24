using Tausi.Domain.Common;

namespace Tausi.Domain.Ledger;

public sealed record JournalLineDraft(string AccountCode, decimal Debit, decimal Credit, string Memo = "")
{
    public static JournalLineDraft Dr(string accountCode, decimal amount, string memo = "") => new(accountCode, amount, 0m, memo);
    public static JournalLineDraft Cr(string accountCode, decimal amount, string memo = "") => new(accountCode, 0m, amount, memo);
}

/// <summary>A journal entry to be posted. <see cref="EnsureBalanced"/> enforces double entry.</summary>
public sealed record JournalEntryDraft(
    DateTimeOffset Date,
    string Memo,
    string SourceType,
    string? SourceId,
    IReadOnlyList<JournalLineDraft> Lines,
    Guid? WorkPeriodId = null,
    Guid? ShiftId = null)
{
    public decimal TotalDebit => Lines.Sum(l => l.Debit);
    public decimal TotalCredit => Lines.Sum(l => l.Credit);

    public void EnsureBalanced()
    {
        if (Lines.Count < 2)
            throw new UnbalancedEntryException("A journal entry needs at least two lines.");
        foreach (var line in Lines)
        {
            if (line.Debit < 0m || line.Credit < 0m)
                throw new UnbalancedEntryException("Debits and credits cannot be negative.");
            if ((line.Debit == 0m) == (line.Credit == 0m))
                throw new UnbalancedEntryException("Each line must have either a debit or a credit, not both or neither.");
            if (decimal.Round(line.Debit, 2) != line.Debit || decimal.Round(line.Credit, 2) != line.Credit)
                throw new UnbalancedEntryException("Ledger amounts are in whole cents.");
        }
        if (TotalDebit != TotalCredit)
            throw new UnbalancedEntryException($"Debits ({TotalDebit:0.00}) must equal credits ({TotalCredit:0.00}).");
    }
}

public class UnbalancedEntryException(string message) : DomainException(message);
