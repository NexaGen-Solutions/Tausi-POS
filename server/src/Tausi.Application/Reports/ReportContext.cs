using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Money;
using Tausi.Application.Settings;

namespace Tausi.Application.Reports;

/// <summary>
/// The period a report covers: dates (midnight to midnight in the business's time zone) or one work
/// period (from its opening to its close). Everything counts when it happened inside the window
/// [<see cref="Start"/>, <see cref="End"/>): a line when it was served, a payment when it was taken.
/// <see cref="PeriodIds"/> are the work periods that ran during it.
/// </summary>
public sealed record ReportPeriod(
    IReadOnlyList<Guid> PeriodIds, DateOnly From, DateOnly To, DateTimeOffset Start, DateTimeOffset End, bool HasOpenPeriod, string Label)
{
    public bool Contains(DateOnly date) => date >= From && date <= To;
}

/// <summary>What a report runs with: the database, the filters, the period, names and the business's clock.</summary>
public sealed class ReportContext
{
    private ReportContext(ITausiDb db, ReportFilters filters, ReportPeriod period, TimeZoneInfo zone, DateTimeOffset now, string business,
        string currency, IReadOnlyDictionary<Guid, (string Name, string Role)> users, IReadOnlyDictionary<Guid, string> terminals)
    {
        Db = db;
        Filters = filters;
        Period = period;
        Zone = zone;
        Now = now;
        Business = business;
        Currency = currency;
        Users = users;
        Terminals = terminals;
    }

    public ITausiDb Db { get; }
    public ReportFilters Filters { get; }
    public ReportPeriod Period { get; }
    public TimeZoneInfo Zone { get; }
    public DateTimeOffset Now { get; }
    public string Business { get; }
    public string Currency { get; }
    public IReadOnlyDictionary<Guid, (string Name, string Role)> Users { get; }
    public IReadOnlyDictionary<Guid, string> Terminals { get; }

    /// <summary>Open tickets count: on by default for the current work period, never when no open period is in range.</summary>
    public bool IncludeOpen => Period.HasOpenPeriod && (Filters.IncludeOpen ?? true);

    public string UserName(Guid? id) => id is { } u && Users.TryGetValue(u, out var x) ? x.Name : "";
    public string RoleOf(Guid id) => Users.TryGetValue(id, out var x) ? x.Role : "";
    public string TerminalName(Guid? id) => id is { } t && Terminals.TryGetValue(t, out var n) ? n : "";

    /// <summary>The tickets active during the period: opened before it ended, and still open or last used after it started.</summary>
    public IQueryable<Domain.Service.Ticket> Tickets()
    {
        var (start, end) = (Period.Start, Period.End);
        return Db.Tickets.AsNoTracking()
            .Where(k => k.OpenedAt < end && (k.Status == Domain.Service.TicketStatus.Open || k.LastActivityAt >= start));
    }

    /// <summary>A time in the business's own clock.</summary>
    public DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone);

    public static async Task<ReportContext> Create(ITausiDb db, ReportFilters filters, TimeProvider clock, CancellationToken ct)
    {
        var zone = await BusinessDates.Zone(db, ct);
        var now = clock.GetUtcNow();
        var settings = await SettingsRegistry.Load(db, ct);
        var period = await ResolvePeriod(db, filters, zone, now, ct);
        var users = await db.Users.AsNoTracking()
            .Select(u => new { u.Id, u.Name, Role = u.Role != null ? u.Role.Name : "" })
            .ToDictionaryAsync(u => u.Id, u => (u.Name, u.Role), ct);
        var terminals = await db.Terminals.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return new ReportContext(db, filters, period, zone, now, settings.String(SettingsRegistry.BusinessName),
            settings.String(SettingsRegistry.BusinessCurrency), users, terminals);
    }

    /// <summary>
    /// A work period (asked for, else the open one, else the last closed), or a range of business
    /// dates. Work periods are few (one a day), so their dates are worked out here, also for any
    /// from before business dates were stored.
    /// </summary>
    public static async Task<ReportPeriod> ResolvePeriod(ITausiDb db, ReportFilters f, TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct)
    {
        var periods = (await db.WorkPeriods.AsNoTracking()
                .Select(p => new { p.Id, p.OpenedAt, p.ClosedAt, p.BusinessDate, p.IsOpen }).ToListAsync(ct))
            .Select(p => new { p.Id, p.OpenedAt, End = p.ClosedAt ?? DateTimeOffset.MaxValue, p.IsOpen, Date = p.BusinessDate ?? BusinessDates.Of(p.OpenedAt, zone) })
            .OrderBy(p => p.OpenedAt).ToList();
        var today = BusinessDates.Of(now, zone);

        DateOnly? from = f.From, to = f.To ?? f.From;
        if (f.Quick is { } quick) (from, to) = Range(quick, today);
        if (from is { } start && to is { } end)
        {
            if (end < start) throw new FieldErrorsException(new Dictionary<string, string> { ["to"] = "The end date is before the start date." });
            if (end.DayNumber - start.DayNumber > 366)
                throw new FieldErrorsException(new Dictionary<string, string> { ["to"] = "Choose at most a year." });
            var midnight = Midnight(start, zone);
            var afterEnd = Midnight(end.AddDays(1), zone);
            var ran = periods.Where(p => p.OpenedAt < afterEnd && p.End > midnight).ToList();
            return new ReportPeriod(ran.Select(p => p.Id).ToList(), start, end, midnight, afterEnd, ran.Any(p => p.IsOpen), RangeLabel(start, end));
        }

        var chosen = f.WorkPeriodId is { } id
            ? periods.FirstOrDefault(p => p.Id == id) ?? throw new NotFoundException("Work period not found.")
            : periods.LastOrDefault(p => p.IsOpen) ?? periods.LastOrDefault();
        // An open work period's window has no end yet: everything up to now (and at this very moment) counts.
        if (chosen is null) return new ReportPeriod([], today, today, Midnight(today, zone), DateTimeOffset.MaxValue, false, "No work period yet");
        var label = $"{chosen.Date.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)}{(chosen.IsOpen ? " (open)" : "")}";
        return new ReportPeriod([chosen.Id], chosen.Date, chosen.Date, chosen.OpenedAt, chosen.End, chosen.IsOpen, label);
    }

    public static (DateOnly From, DateOnly To) Range(QuickRange quick, DateOnly today)
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        return quick switch
        {
            QuickRange.Today => (today, today),
            QuickRange.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            QuickRange.ThisWeek => (monday, today),
            QuickRange.LastWeek => (monday.AddDays(-7), monday.AddDays(-1)),
            QuickRange.ThisMonth => (firstOfMonth, today),
            _ => (firstOfMonth.AddMonths(-1), firstOfMonth.AddDays(-1)),
        };
    }

    private static string RangeLabel(DateOnly from, DateOnly to) =>
        from == to
            ? from.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)
            : $"{from.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} – {to.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";

    public static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo zone) => BusinessDates.Midnight(date, zone);
}
