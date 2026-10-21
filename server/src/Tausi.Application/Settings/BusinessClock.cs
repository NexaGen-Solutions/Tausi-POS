using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.Settings;

namespace Tausi.Application.Settings;

/// <summary>
/// The business's own time (setting <c>business.timeZone</c>), whatever time zone the server runs in.
/// Use it for every printed time and date: bills, slips, the test print, and receipts and reports.
/// </summary>
public sealed class BusinessClock(ITausiDb db, TimeProvider clock)
{
    public async Task<TimeZoneInfo> Zone(CancellationToken ct)
    {
        var name = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).Select(s => s.TimeZone).FirstOrDefaultAsync(ct);
        return BusinessTimeZone.Find(name);
    }

    /// <summary>Now, in the business's time zone.</summary>
    public async Task<DateTimeOffset> Now(CancellationToken ct) => ToLocal(clock.GetUtcNow(), await Zone(ct));

    public static DateTimeOffset ToLocal(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone);
}
