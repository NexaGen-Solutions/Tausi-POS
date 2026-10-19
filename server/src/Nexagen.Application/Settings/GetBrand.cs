using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;

namespace Nexagen.Application.Settings;

public sealed class GetBrand(INexagenDb db)
{
    public async Task<BrandDto> Execute(CancellationToken ct)
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s is null ? new BrandDto("", null, "KES") : new BrandDto(s.Name, s.LogoFileId, s.Currency);
    }
}
