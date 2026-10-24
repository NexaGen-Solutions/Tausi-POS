using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;

namespace Tausi.Application.Settings;

/// <summary>The public face of the business, shown before login and in the admin sidebar. The logo is the screen-sized copy.</summary>
public sealed record BrandDto(string Name, Guid? LogoFileId, string Currency);

public sealed class GetBrand(ITausiDb db)
{
    public async Task<BrandDto> Execute(CancellationToken ct)
    {
        var s = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        return s is null ? new BrandDto("", null, "KES") : new BrandDto(s.Name, s.LogoWebFileId ?? s.LogoFileId, s.Currency);
    }
}
