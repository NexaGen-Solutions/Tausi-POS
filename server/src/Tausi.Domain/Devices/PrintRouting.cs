namespace Tausi.Domain.Devices;

/// <summary>Where a terminal's Print button sends its slips, in words for the admin screen.</summary>
public static class PrintRouting
{
    /// <summary>
    /// "Print on Counter A: 1. Bill → Ticket Printer 1 · 2. Dispense slip "Counter A" → Ticket Printer 1".
    /// The order is the one-button order: the bill first, then the dispense slip.
    /// </summary>
    public static string Preview(string terminal, string? receiptPrinter, string dispenseHeader, string? dispensePrinter) =>
        $"Print on {terminal}: 1. Bill → {receiptPrinter ?? "no printer set"} · " +
        $"2. Dispense slip \"{dispenseHeader}\" → {dispensePrinter ?? "no printer set"}";
}
