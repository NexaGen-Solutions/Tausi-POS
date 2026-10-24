using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.Settings;

namespace Tausi.Application.Money;

/// <summary>
/// Business dates: a work period's date is the day it was opened, in the business's time zone (a
/// Friday that runs past midnight is still Friday); entries made on the admin site take the date
/// chosen on the form, never in the future and back-dated at most <c>books.backdateDays</c>.
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

    /// <summary>The day's work periods are all closed (so an entry for it now is a late entry).</summary>
    public static async Task<bool> IsClosed(ITausiDb db, DateOnly date, CancellationToken ct)
    {
        var zone = await Zone(db, ct);
        var periods = (await db.WorkPeriods.AsNoTracking().Select(p => new { p.BusinessDate, p.OpenedAt, p.IsOpen }).ToListAsync(ct))
            .Where(p => (p.BusinessDate ?? Of(p.OpenedAt, zone)) == date).ToList();
        return periods.Count > 0 && periods.All(p => !p.IsOpen);
    }

    /// <summary>The work periods of a business date (older ones without a stored date worked out).</summary>
    public static async Task<List<Guid>> PeriodsOn(ITausiDb db, DateOnly date, CancellationToken ct)
    {
        var zone = await Zone(db, ct);
        return (await db.WorkPeriods.AsNoTracking().Select(p => new { p.Id, p.BusinessDate, p.OpenedAt }).ToListAsync(ct))
            .Where(p => (p.BusinessDate ?? Of(p.OpenedAt, zone)) == date).Select(p => p.Id).ToList();
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
