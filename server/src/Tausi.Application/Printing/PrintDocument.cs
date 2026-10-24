namespace Tausi.Application.Printing;

/// <summary>A slip ready to print: the ESC/POS bytes and the same slip as readable text.</summary>
public sealed record PrintDocument(byte[] EscPos, string Text);

/// <summary>A 1-bit image, packed 8 pixels per byte, most significant bit first; 1 = black.</summary>
public sealed record MonoBitmap(int Width, int Height, byte[] Data)
{
    public int BytesPerRow => (Width + 7) / 8;
}

/// <summary>Supplies the business logo as a printable bitmap. Returns null when there is no logo.</summary>
public interface ILogoProvider
{
    Task<MonoBitmap?> GetLogo(int maxWidthDots, CancellationToken ct = default);
}

/// <summary>A logo upload, checked: what it really is, and a copy sized for screens (PNG).</summary>
public sealed record LogoImage(string ContentType, byte[] WebPng);

/// <summary>Reads uploaded logos and keeps the printable 1-bit versions up to date.</summary>
public interface ILogoImages
{
    /// <summary>The image, if the bytes are a PNG or JPEG picture; null otherwise.</summary>
    LogoImage? Read(byte[] bytes);

    /// <summary>Drops the cached print rasters and makes them again for the current logo (80 and 58 mm).</summary>
    Task Refresh(CancellationToken ct = default);

    /// <summary>A 1-bit raster as a PNG, to show what the printer prints.</summary>
    byte[] ToPng(MonoBitmap bitmap);
}
