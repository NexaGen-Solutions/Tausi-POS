using SkiaSharp;
using Tausi.Application.Printing;

namespace Tausi.Infrastructure.Printing;

/// <summary>Checks logo uploads with SkiaSharp, makes the screen copy and renews the print rasters.</summary>
public sealed class SkiaLogoImages(ILogoProvider logos) : ILogoImages
{
    /// <summary>The screen copy is at most this wide (or high); the PIN screen shows it small.</summary>
    private const int WebMaxSize = 512;

    public LogoImage? Read(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        var type = codec?.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            _ => null,
        };
        if (codec is null || type is null) return null;
        using var source = SKBitmap.Decode(codec);
        if (source is null || source.Width == 0 || source.Height == 0) return null;

        var scale = Math.Min(1.0, (double)WebMaxSize / Math.Max(source.Width, source.Height));
        using var web = scale < 1.0
            ? source.Resize(new SKImageInfo((int)(source.Width * scale), (int)(source.Height * scale)), new SKSamplingOptions(SKFilterMode.Linear))
            : source.Copy();
        using var image = SKImage.FromBitmap(web);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new LogoImage(type, png.ToArray());
    }

    public async Task Refresh(CancellationToken ct = default)
    {
        SkiaLogoProvider.Forget();
        foreach (var width in new[] { ReceiptBuilder.LogoWidthFor(80), ReceiptBuilder.LogoWidthFor(58) })
            await logos.GetLogo(width, ct);
    }

    public byte[] ToPng(MonoBitmap bitmap)
    {
        using var image = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var black = (bitmap.Data[y * bitmap.BytesPerRow + x / 8] & (0x80 >> (x % 8))) != 0;
            image.SetPixel(x, y, black ? SKColors.Black : SKColors.White);
        }
        using var encoded = SKImage.FromBitmap(image).Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
