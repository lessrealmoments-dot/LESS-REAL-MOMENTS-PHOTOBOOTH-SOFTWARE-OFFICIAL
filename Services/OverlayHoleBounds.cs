using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BoothDesktop.Services;

/// <summary>
/// Finds the transparent "window" inside a frame overlay for each template photo slot.
/// dslrBooth packs often use a larger &lt;Photo&gt; rect than the visible hole in the PNG matting.
/// </summary>
internal static class OverlayHoleBounds
{
    private const byte AlphaThreshold = 200;
    private const int MinHolePixels = 64;
    private const double MinHoleAreaFraction = 0.05;

    public static bool TryGetHoleRect(BitmapSource overlay, Rect slotRect, out Rect holeRect)
    {
        holeRect = slotRect;
        if (overlay.PixelWidth <= 0 || overlay.PixelHeight <= 0)
            return false;

        var bgra = EnsurePbgra32(overlay);
        var stride = bgra.PixelWidth * 4;
        var bytes = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(bytes, stride, 0);

        var xStart = Math.Clamp((int)Math.Floor(slotRect.X), 0, bgra.PixelWidth - 1);
        var yStart = Math.Clamp((int)Math.Floor(slotRect.Y), 0, bgra.PixelHeight - 1);
        var xEnd = Math.Clamp((int)Math.Ceiling(slotRect.Right) - 1, 0, bgra.PixelWidth - 1);
        var yEnd = Math.Clamp((int)Math.Ceiling(slotRect.Bottom) - 1, 0, bgra.PixelHeight - 1);
        if (xEnd < xStart || yEnd < yStart)
            return false;

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        var transparentCount = 0;

        for (var y = yStart; y <= yEnd; y++)
        {
            var row = y * stride;
            for (var x = xStart; x <= xEnd; x++)
            {
                if (bytes[row + x * 4 + 3] >= AlphaThreshold) continue;
                transparentCount++;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        var slotPixels = (xEnd - xStart + 1) * (yEnd - yStart + 1);
        if (transparentCount < MinHolePixels
            || transparentCount < slotPixels * MinHoleAreaFraction)
            return false;

        holeRect = new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return true;
    }

    private static BitmapSource EnsurePbgra32(BitmapSource src)
    {
        if (src.Format == PixelFormats.Pbgra32)
            return src;
        var converted = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        return converted;
    }
}
