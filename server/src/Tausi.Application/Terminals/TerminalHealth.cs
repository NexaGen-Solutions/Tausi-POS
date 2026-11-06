using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;

namespace Tausi.Application.Terminals;

/// <summary>What the terminal app reports about itself (its state.json, shared with the launcher). Times are seconds since 1970.</summary>
public sealed record TerminalHealthReport(string? Version, TerminalHealthReport.ExitReport? LastExit, TerminalHealthReport.UpdateReport? LastUpdate)
{
    public sealed record ExitReport(string? Reason, long At);
    public sealed record UpdateReport(string? From, string? To, string? Result, string? Error, long At);
}

/// <summary>A terminal's health on the Terminals page; <see cref="Warning"/> when something needs looking at.</summary>
public sealed record TerminalHealthDto(
    string? UpdateFrom, string? UpdateTo, string? UpdateResult, string? UpdateError, DateTimeOffset? UpdateAt,
    string? ExitReason, DateTimeOffset? ExitAt, string? Warning);

/// <summary>
/// Keeps what each terminal app reports about its updates and why it last stopped (Task 17a), so the
/// Terminals page can say "Counter A: last update failed (blocked by antivirus)". The terminal reports
/// it over its live connection when it starts; nothing it sends is trusted beyond being shown.
/// </summary>
public static class TerminalHealth
{
    /// <summary>An update still "Installing" after this long was cut off.</summary>
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(15);

    public static async Task Save(ITausiDb db, Guid terminalId, TerminalHealthReport report, CancellationToken ct)
    {
        var update = report.LastUpdate;
        var exit = report.LastExit;
        await db.Terminals.Where(t => t.Id == terminalId).ExecuteUpdateAsync(u => u
            .SetProperty(t => t.LastUpdateFrom, Cut(update?.From, 50))
            .SetProperty(t => t.LastUpdateTo, Cut(update?.To, 50))
            .SetProperty(t => t.LastUpdateResult, Cut(update?.Result, 20))
            .SetProperty(t => t.LastUpdateError, Cut(update?.Error, 300))
            .SetProperty(t => t.LastUpdateAt, Time(update?.At))
            .SetProperty(t => t.LastExitReason, Cut(exit?.Reason, 100))
            .SetProperty(t => t.LastExitAt, Time(exit?.At)), ct);
    }

    public static TerminalHealthDto? Of(Domain.Devices.Terminal t, DateTimeOffset now)
    {
        if (t.LastUpdateResult is null && t.LastExitReason is null) return null;
        return new TerminalHealthDto(t.LastUpdateFrom, t.LastUpdateTo, t.LastUpdateResult, t.LastUpdateError, t.LastUpdateAt,
            t.LastExitReason, t.LastExitAt, Warning(t, now));
    }

    /// <summary>"Last update failed (blocked by antivirus)", "Last update rolled back: …", "Stopped unexpectedly (exit code 1)"; null when all is well.</summary>
    public static string? Warning(Domain.Devices.Terminal t, DateTimeOffset now)
    {
        string Why(string text) => string.IsNullOrWhiteSpace(t.LastUpdateError) ? text : $"{text} ({t.LastUpdateError.TrimEnd('.')})";
        var version = t.LastUpdateTo is { Length: > 0 } to ? $" to {to}" : "";
        return t.LastUpdateResult switch
        {
            "Failed" => Why($"Last update{version} failed"),
            "Rolled back" => Why($"Last update{version} was rolled back to {t.LastUpdateFrom}"),
            "Installing" when t.LastUpdateAt is { } at && now - at > InstallTimeout => $"Last update{version} didn't finish",
            _ when t.LastExitReason?.StartsWith("Stopped unexpectedly", StringComparison.Ordinal) == true => t.LastExitReason,
            _ => null,
        };
    }

    private static string? Cut(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length <= max ? text.Trim() : text.Trim()[..max];

    private static DateTimeOffset? Time(long? seconds) =>
        seconds is > 0 and < 32503680000 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
}
