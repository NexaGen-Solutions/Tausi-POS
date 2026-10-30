using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;
using Tausi.Infrastructure.Persistence;

namespace Tausi.LoadTest;

public sealed record Options(
    Uri Server, string AdminPassword, string SeedOutput, string Provider, string Db, string PrintOutput,
    double Hours, double Speed, int Waiters, double TicketsPerMinute, double VoidShare, string? Out)
{
    public static Options Parse(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != $"--{name}").Skip(1).FirstOrDefault();
        string Required(string name) => Arg(name) ?? throw new ArgumentException($"--{name} is required.");
        return new Options(
            new Uri(Required("server")), Required("admin-password"), Required("seed-output"), Arg("provider") ?? "Postgres",
            Required("db"), Required("print-output"),
            double.Parse(Arg("hours") ?? "6", System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(Arg("speed") ?? "3", System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(Arg("waiters") ?? "12"),
            double.Parse(Arg("tickets-per-minute") ?? "3", System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(Arg("void-share") ?? "0.03", System.Globalization.CultureInfo.InvariantCulture),
            Arg("out"));
    }
}

/// <summary>Counts in the database before and after, its size, and the server's memory.</summary>
public sealed record Snapshot(
    long Tickets, long Settled, decimal Quantity, long Bills, long Dispense, long VoidSlips, long Receipts,
    long UnsentJobs, long Payments, decimal PaymentTotal, long VoidLogs, long SaleEntries, long DatabaseBytes);

public static class Database
{
    private static TausiDbContext Context(Options o) => o.Provider == "SqlServer"
        ? new SqlServerTausiDbContext(new DbContextOptionsBuilder<SqlServerTausiDbContext>().UseSqlServer(o.Db).Options)
        : new PostgresTausiDbContext(new DbContextOptionsBuilder<PostgresTausiDbContext>().UseNpgsql(o.Db).Options);

    public static async Task<Snapshot> Snapshot(Options o)
    {
        await using var db = Context(o);
        long Jobs(PrintJobKind kind) => db.PrintJobs.LongCount(j => j.Kind == kind);
        var size = o.Provider == "SqlServer"
            ? (await db.Database.SqlQueryRaw<long>("SELECT CAST(SUM(CAST(size AS bigint)) * 8192 AS bigint) AS [Value] FROM sys.database_files").ToListAsync()).Single()
            : (await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").ToListAsync()).Single();
        return new Snapshot(
            await db.Tickets.LongCountAsync(),
            await db.Tickets.LongCountAsync(t => t.Status == TicketStatus.Settled),
            await db.OrderLines.SumAsync(l => (decimal?)l.Quantity) ?? 0,
            Jobs(PrintJobKind.Bill), Jobs(PrintJobKind.Dispense), Jobs(PrintJobKind.Void), Jobs(PrintJobKind.Receipt),
            await db.PrintJobs.LongCountAsync(j => j.Status != PrintJobStatus.Sent),
            await db.Payments.LongCountAsync(p => p.Status != PaymentStatus.Removed),
            await db.Payments.Where(p => p.Status != PaymentStatus.Removed).SumAsync(p => (decimal?)p.Amount) ?? 0,
            await db.VoidLogs.LongCountAsync(),
            await db.JournalEntries.LongCountAsync(e => e.SourceType == "Sale"),
            size);
    }

    /// <summary>The server's working set, when it runs on this machine as TausiServer.</summary>
    public static long? ServerMemory()
    {
        var process = Process.GetProcessesByName("TausiServer").FirstOrDefault();
        if (process is null) return null;
        process.Refresh();
        return process.WorkingSet64;
    }
}

/// <summary>
/// The File printer writes each slip to print-output/. The "printed" time runs from pressing Print to
/// the file of its dispense slip (the second of the Print's two slips), matched by the ticket number
/// the slip shows.
/// </summary>
public sealed partial class PrintWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Stats _stats;
    private readonly ConcurrentDictionary<long, ConcurrentQueue<long>> _waiting = new();

    public PrintWatcher(string folder, Stats stats)
    {
        _stats = stats;
        Directory.CreateDirectory(folder);
        _watcher = new FileSystemWatcher(folder, "*-Dispense-*.txt") { EnableRaisingEvents = true };
        _watcher.Created += (_, e) => _ = Task.Run(() => Seen(e.FullPath));
    }

    public void Expect(long ticketNumber) =>
        _waiting.GetOrAdd(ticketNumber, _ => new ConcurrentQueue<long>()).Enqueue(Stopwatch.GetTimestamp());

    private async Task Seen(string path)
    {
        var seen = Stopwatch.GetTimestamp();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var text = await File.ReadAllTextAsync(path);
                if (TicketNumber().Match(text) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var number)
                    && _waiting.TryGetValue(number, out var queue) && queue.TryDequeue(out var start))
                {
                    _stats.Record("printed", Stopwatch.GetElapsedTime(start, seen).TotalMilliseconds);
                    return;
                }
                if (text.Length > 0) break;
            }
            catch (IOException) { }
            await Task.Delay(20);
        }
        _stats.Count("unmatched slips");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"#(\d{3,})")]
    private static partial System.Text.RegularExpressions.Regex TicketNumber();

    public void Dispose() => _watcher.Dispose();
}

public sealed record Target(string Name, string Metric, double Percentile, double LimitMs);

public sealed record ReportResult(bool Passed, string Markdown, IReadOnlyDictionary<string, double?> P95, IReadOnlyList<string> Problems);

public static class Report
{
    public static readonly Target[] Targets =
    [
        new("Add a line", "add", 95, 150),
        new("Print, until queued", "print", 95, 300),
        new("Print, until the File printer wrote it", "printed", 95, 1000),
        new("Settle", "settle", 95, 400),
        new("Table map on another terminal", "map", 95, 500),
    ];

    public static ReportResult Build(Options o, Stats s, Snapshot before, Snapshot after, JsonElement check,
        List<(double Hours, long Bytes)> memory, TimeSpan elapsed)
    {
        var problems = new List<string>();
        var lines = new List<string>
        {
            $"Run: {o.Hours} simulated hours at {o.Speed}× ({elapsed:hh\\:mm\\:ss} real), {o.Provider}, {s.CountOf("tickets")} tickets, " +
            $"{s.CountOf("prints")} prints, {s.CountOf("voids")} voids, {s.CountOf("settled")} settled, {s.CountOf("payments")} payments, " +
            $"{s.CountOf("map events")} table-map events seen by another terminal.",
            "",
            "| Measure | p50 | p95 | Target (p95) | Samples | Met |",
            "| --- | --- | --- | --- | --- | --- |",
        };
        var p95 = new Dictionary<string, double?>();
        foreach (var t in Targets)
        {
            var p = s.Percentile(t.Metric, t.Percentile);
            p95[t.Metric] = p;
            var met = p is not null && p <= t.LimitMs;
            if (!met) problems.Add($"{t.Name}: p95 {p?.ToString("0") ?? "no samples"} ms, target under {t.LimitMs} ms.");
            lines.Add($"| {t.Name} | {s.Percentile(t.Metric, 50):0} ms | {p:0} ms | under {t.LimitMs:0} ms | {s.Samples(t.Metric)} | {(met ? "yes" : "**no**")} |");
        }

        // Nothing lost or doubled: every difference here is a problem.
        void Same(string what, decimal expected, decimal actual)
        {
            if (expected != actual) problems.Add($"{what}: expected {expected}, the database has {actual}.");
        }
        Same("Tickets", s.CountOf("tickets"), after.Tickets - before.Tickets);
        Same("Settled tickets", s.CountOf("settled"), after.Settled - before.Settled);
        Same("Items (quantity)", s.CountOf("quantity"), after.Quantity - before.Quantity);
        Same("Bills printed", s.CountOf("prints") + s.CountOf("voids"), after.Bills - before.Bills);
        Same("Dispense slips", s.CountOf("prints"), after.Dispense - before.Dispense);
        Same("VOID slips", s.CountOf("voids"), after.VoidSlips - before.VoidSlips);
        Same("Receipts", s.CountOf("settled"), after.Receipts - before.Receipts);
        Same("Payments", s.CountOf("payments"), after.Payments - before.Payments);
        Same("Payment total", s.SumOf("payments"), after.PaymentTotal - before.PaymentTotal);
        Same("Void logs", s.CountOf("voids"), after.VoidLogs - before.VoidLogs);
        Same("Sale journal entries", s.CountOf("settled"), after.SaleEntries - before.SaleEntries);
        if (after.UnsentJobs != before.UnsentJobs) problems.Add($"{after.UnsentJobs - before.UnsentJobs} print jobs were not sent.");
        if (!check.GetProperty("ok").GetBoolean())
            foreach (var p in check.GetProperty("problems").EnumerateArray())
                problems.Add($"Check: {p.GetProperty("area").GetString()}: {p.GetProperty("detail").GetString()}");
        lines.Add("");
        lines.Add($"Trial balance: debit {check.GetProperty("totalDebit").GetDecimal():0.00}, credit {check.GetProperty("totalCredit").GetDecimal():0.00}.");
        problems.AddRange(s.Errors.Take(20).Select(e => $"Error: {e}"));
        if (s.Errors.Count > 20) problems.Add($"…and {s.Errors.Count - 20} more errors.");

        // Memory: growth over the last two thirds of the run (the last 4 of 6 hours) under 10%.
        if (memory.Count >= 3)
        {
            var from = memory.Where(m => m.Hours >= o.Hours / 3).OrderBy(m => m.Hours).FirstOrDefault();
            var last = memory.OrderBy(m => m.Hours).Last();
            if (from.Bytes > 0)
            {
                var growth = (double)(last.Bytes - from.Bytes) / from.Bytes;
                lines.Add($"Server memory: {from.Bytes / 1048576} MB at {from.Hours:0.0} h, {last.Bytes / 1048576} MB at {last.Hours:0.0} h ({growth:+0.0%;-0.0%}).");
                if (growth >= 0.10) problems.Add($"Server memory grew {growth:0.0%} over the last part of the run (target under 10%).");
            }
        }
        else lines.Add("Server memory: not measured (the server isn't a TausiServer process on this machine).");

        var added = after.Tickets - before.Tickets;
        if (added > 0)
            lines.Add($"Database: {(after.DatabaseBytes - before.DatabaseBytes) * 1000.0 / added / 1048576:0.0} MB per 1,000 tickets " +
                      $"({after.DatabaseBytes / 1048576} MB in all).");

        lines.Add("");
        lines.Add(problems.Count == 0 ? "**Every target met; nothing lost or doubled.**" : "**Problems:**");
        lines.AddRange(problems.Select(p => $"- {p}"));
        return new ReportResult(problems.Count == 0, string.Join('\n', lines), p95, problems);
    }
}
