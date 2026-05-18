using System.Windows;
using NetVips;

namespace BoothDesktop.Services.Vips;

/// <summary>
/// libvips port of <see cref="OverlayHoleBounds"/>. Same semantics, no WPF / BitmapSource
/// dependency, no full-image pixel copy in managed memory.
///
/// Workflow per slot:
///   1. Crop the overlay's alpha band to the slot rect.
///   2. Build a binary mask where alpha &lt; <see cref="AlphaThreshold"/> = 255 (hole) else 0.
///   3. Use <c>mask.Avg()</c> to count hole pixels and validate against
///      <see cref="MinHolePixels"/> + <see cref="MinHoleAreaFraction"/> guards.
///   4. <c>mask.FindTrim(background: [0])</c> returns the bounding box of the hole pixels
///      in cropped-image coordinates; add the crop offset back to get absolute coordinates.
///
/// All operations stay inside libvips so the cropped alpha never round-trips through a managed
/// byte[] (cheaper than the WPF implementation, which copies the full overlay's pixels once).
/// </summary>
internal static class VipsOverlayHoleBounds
{
    private const int AlphaThreshold = 200;
    private const int MinHolePixels = 64;
    private const double MinHoleAreaFraction = 0.05;

    /// <summary>
    /// Extracts the alpha band from an overlay image and materialises it into a memory-backed
    /// libvips image so subsequent <see cref="TryGetHoleRect"/> calls can be invoked many times
    /// without re-reading the source file. Returns null when the overlay has no alpha channel.
    /// </summary>
    public static Image? PrepareAlphaBand(Image overlay)
    {
        if (overlay.Bands < 2 || !overlay.HasAlpha())
            return null;

        // Last band = alpha for both RGBA (4 bands) and LA (2 bands) layouts.
        var alpha = overlay[overlay.Bands - 1];

        // Materialise so repeated crops/projections don't re-pull tiles through the load pipeline.
        var bytes = alpha.WriteToMemory();
        return Image.NewFromMemory(bytes, alpha.Width, alpha.Height, 1, alpha.Format);
    }

    public static bool TryGetHoleRect(Image alphaBand, Rect slotRect, out Rect holeRect)
    {
        holeRect = slotRect;
        if (alphaBand.Width <= 0 || alphaBand.Height <= 0)
            return false;

        var xStart = Math.Clamp((int)Math.Floor(slotRect.X), 0, alphaBand.Width - 1);
        var yStart = Math.Clamp((int)Math.Floor(slotRect.Y), 0, alphaBand.Height - 1);
        var xEnd = Math.Clamp((int)Math.Ceiling(slotRect.Right) - 1, 0, alphaBand.Width - 1);
        var yEnd = Math.Clamp((int)Math.Ceiling(slotRect.Bottom) - 1, 0, alphaBand.Height - 1);
        if (xEnd < xStart || yEnd < yStart)
            return false;

        var cropW = xEnd - xStart + 1;
        var cropH = yEnd - yStart + 1;

        Image cropped = null!;
        Image mask = null!;
        try
        {
            cropped = alphaBand.Crop(xStart, yStart, cropW, cropH);

            // Image '<' double operator returns 255 where alpha < threshold (= hole), 0 otherwise.
            mask = (cropped < (double)AlphaThreshold).Cast(NetVips.Enums.BandFormat.Uchar);

            // mask.Avg() is in [0, 255]; multiply back to get hole-pixel count.
            var avg = mask.Avg();
            var slotPixels = cropW * cropH;
            var transparentCount = (int)Math.Round(avg * slotPixels / 255.0);

            if (transparentCount < MinHolePixels
                || transparentCount < slotPixels * MinHoleAreaFraction)
                return false;

            // FindTrim returns object[] { left, top, width, height } - bounding box of
            // non-background content. With background=[0] the bbox covers all 255-valued
            // pixels (= the hole).
            var trim = mask.FindTrim(threshold: 1.0, background: new double[] { 0.0 });
            var left = Convert.ToInt32(trim[0]);
            var top = Convert.ToInt32(trim[1]);
            var width = Convert.ToInt32(trim[2]);
            var height = Convert.ToInt32(trim[3]);

            if (width <= 0 || height <= 0)
                return false;

            holeRect = new Rect(xStart + left, yStart + top, width, height);
            return true;
        }
        finally
        {
            cropped?.Dispose();
            mask?.Dispose();
        }
    }
}
