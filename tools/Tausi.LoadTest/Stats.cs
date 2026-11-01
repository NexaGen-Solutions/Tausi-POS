using System.Collections.Concurrent;

namespace Tausi.LoadTest;

/// <summary>Latencies per metric (milliseconds) and counts of what the test did, for the targets and the after-run check.</summary>
public sealed class Stats
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<double>> _times = new();
    private readonly ConcurrentDictionary<string, long> _counts = new();
    private readonly ConcurrentDictionary<string, decimal> _sums = new();
    private readonly ConcurrentQueue<string> _errors = new();

    public void Record(string metric, double milliseconds) => _times.GetOrAdd(metric, _ => []).Add(milliseconds);
    public void Count(string what, long by = 1) => _counts.AddOrUpdate(what, by, (_, n) => n + by);
    public void Sum(string what, decimal by) => _sums.AddOrUpdate(what, by, (_, n) => n + by);
    public void Error(string error) => _errors.Enqueue(error);

    public long CountOf(string what) => _counts.GetValueOrDefault(what);
    public decimal SumOf(string what) => _sums.GetValueOrDefault(what);
    public IReadOnlyList<string> Errors => [.. _errors];

    public int Samples(string metric) => _times.TryGetValue(metric, out var bag) ? bag.Count : 0;

    /// <summary>The p-th percentile (0–100), nearest rank; null without samples.</summary>
    public double? Percentile(string metric, double p)
    {
        if (!_times.TryGetValue(metric, out var bag) || bag.IsEmpty) return null;
        var sorted = bag.Order().ToArray();
        var rank = (int)Math.Ceiling(p / 100 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
