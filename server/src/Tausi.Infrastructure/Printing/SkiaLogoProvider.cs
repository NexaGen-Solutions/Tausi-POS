using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tausi.Application.Abstractions;
using Tausi.Application.Files;
using Tausi.Application.Printing;
using SkiaSharp;

namespace Tausi.Infrastructure.Printing;

/// <summary>Turns the business logo into a 1-bit raster for the printer, cached per file and width.</summary>
public sealed class SkiaLogoProvider(ITausiDb db, IFileStorage files, ILogger<SkiaLogoProvider> log) : ILogoProvider
{
    private const int MaxHeightDots = 200;
    private static readonly ConcurrentDictionary<(Guid, int), MonoBitmap?> Cache = new();

    public async Task<MonoBitmap?> GetLogo(int maxWidthDots, CancellationToken ct = default)
    {
        var fileId = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).Select(s => s.LogoFileId).FirstOrDefaultAsync(ct);
        if (fileId is not { } id) return null;
        return Cache.GetOrAdd((id, maxWidthDots), key => Load(key.Item1, key.Item2));
    }

    private MonoBitmap? Load(Guid fileId, int maxWidth)
    {
        using var stream = files.Open(fileId);
        if (stream is null) return null;
        try
        {
            using var source = SKBitmap.Decode(stream);
            return source is null ? null : ToMono(source, maxWidth, MaxHeightDots);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read logo {FileId}; printing without it", fileId);
            return null;
        }
    }

    public static MonoBitmap ToMono(SKBitmap source, int maxWidth, int maxHeight)
    {
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        var width = Math.Max(1, (int)(source.Width * scale));
        var height = Math.Max(1, (int)(source.Height * scale));
        using var resized = source.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear)) ?? source;

        var bytesPerRow = (width + 7) / 8;
        var data = new byte[bytesPerRow * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var c = resized.GetPixel(x, y);
            // Transparent counts as paper (white); dark pixels print.
            var luminance = (0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue) * c.Alpha / 255 + 255 - c.Alpha;
            if (luminance < 128)
                data[y * bytesPerRow + x / 8] |= (byte)(0x80 >> (x % 8));
        }
        return new MonoBitmap(width, height, data);
    }
}
