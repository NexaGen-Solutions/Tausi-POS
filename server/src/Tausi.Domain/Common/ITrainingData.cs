namespace Tausi.Domain.Common;

/// <summary>
/// Data that a training terminal makes: its tickets, lines, payments, shifts and logs. Training data
/// is kept apart from real sales (reports, the ledger, stock and cash-ups never see it) and can be
/// cleared. Any future integration (such as an ERPNext sync) skips rows with <see cref="IsTraining"/> set.
/// </summary>
public interface ITrainingData
{
    /// <summary>Set when the row is saved from a training terminal; never changed afterwards.</summary>
    bool IsTraining { get; set; }
}
