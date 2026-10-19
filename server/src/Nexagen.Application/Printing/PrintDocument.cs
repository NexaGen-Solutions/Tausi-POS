namespace Nexagen.Application.Printing;

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
