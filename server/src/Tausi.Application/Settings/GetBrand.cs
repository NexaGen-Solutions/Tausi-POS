using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;

namespace Tausi.Application.Settings;

public sealed class GetBrand(ITausiDb db)
{
    public async Task<BrandDto> Execute(CancellationToken ct)
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s is null ? new BrandDto("", null, "KES") : new BrandDto(s.Name, s.LogoFileId, s.Currency);
    }
}
