using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.Settings;

namespace Tausi.Application.Money;

/// <summary>
/// Business dates: everything that happens during service belongs to the calendar date it happened
/// in the business's time zone, the day changing at midnight, whichever work period is open (a sale
/// at 01:30 on Saturday is Saturday's). A work period keeps the date it was opened, as its name.
/// Entries made on the admin site take the date chosen on the form, never in the future and
/// back-dated at most <c>books.backdateDays</c>.
/// </summary>
public static class BusinessDates
{
    public static async Task<TimeZoneInfo> Zone(ITausiDb db, CancellationToken ct) =>
        BusinessTimeZone.Find((await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessTimeZoneKey));

    public static DateOnly Of(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    public static async Task<DateOnly> Today(ITausiDb db, TimeProvider clock, CancellationToken ct) => Of(clock.GetUtcNow(), await Zone(db, ct));

    /// <summary>A work period's business date (stored when it opens; worked out for older ones).</summary>
    public static async Task<DateOnly?> OfPeriod(ITausiDb db, Guid periodId, CancellationToken ct)
    {
        var period = await db.WorkPeriods.AsNoTracking().Where(p => p.Id == periodId)
            .Select(p => new { p.BusinessDate, p.OpenedAt }).FirstOrDefaultAsync(ct);
        if (period is null) return null;
        return period.BusinessDate ?? Of(period.OpenedAt, await Zone(db, ct));
    }

    /// <summary>Midnight at the start of a date in the business's time zone, as a UTC time.</summary>
    public static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>
    /// Every work period that ran on the date is closed (so an entry for it now is a late entry). A date
    /// with no work period isn't closed.
    /// </summary>
    public static async Task<bool> IsClosed(ITausiDb db, DateOnly date, CancellationToken ct)
    {
        var zone = await Zone(db, ct);
        var (start, end) = (Midnight(date, zone), Midnight(date.AddDays(1), zone));
        var periods = await db.WorkPeriods.AsNoTracking()
            .Where(p => p.OpenedAt < end && (p.ClosedAt == null || p.ClosedAt > start)).Select(p => p.IsOpen).ToListAsync(ct);
        return periods.Count > 0 && periods.All(open => !open);
    }

    /// <summary>The date chosen on an admin form: today when none; refused in the future or too far back.</summary>
    public static async Task<DateOnly> Chosen(ITausiDb db, TimeProvider clock, DateOnly? date, CancellationToken ct)
    {
        var today = await Today(db, clock, ct);
        var chosen = date ?? today;
        if (chosen > today) throw new DomainException("The date can't be in the future.", "date_future");
        var days = (await SettingsRegistry.Load(db, ct)).Int(SettingsRegistry.BooksBackdateDays);
        if (chosen < today.AddDays(-days))
            throw new DomainException($"The date can be at most {days} days back.", "date_too_old");
        return chosen;
    }
}
