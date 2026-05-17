using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;

namespace BoothDesktop.Services;

/// <summary>
/// Maps composite PNG onto the printer page in <see cref="GraphicsUnit.Display"/> (1/100 inch).
/// 100% scale = export size at template/300 DPI, centered; alignment adjusts from that baseline.
/// </summary>
internal static class BoothPrintLayout
{
    private const float DefaultExportDpi = 300f;

    public static void DrawImageOnPage(Graphics g, Image image, PrintPageEventArgs e, string? layoutId,
        EffectivePrinterAlignment alignment)
    {
        g.ResetTransform();
        g.PageUnit = GraphicsUnit.Display;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;

        var target = ResolvePrintTargetDisplayUnits(e);
        var (content, dpiUsed) = ResolveContentSizeDisplayUnits(image, layoutId);
        var dest = ComputeAlignedDestRect(content, target, alignment);

        g.DrawImage(image, dest);

        RuntimeLog.Info("Print",
            $"target={target.Width:F0}x{target.Height:F0}@{target.X:F0},{target.Y:F0} " +
            $"content={content.Width:F0}x{content.Height:F0} dest={dest.Width:F0}x{dest.Height:F0} " +
            $"dpi={dpiUsed:F0} px={image.Width}x{image.Height} " +
            $"align={alignment.ScalePercent}% off=({alignment.OffsetXHundredths},{alignment.OffsetYHundredths})");
    }

    internal static RectangleF ComputeAlignedDestRect(SizeF contentSize, RectangleF target,
        EffectivePrinterAlignment alignment)
    {
        var baseline = NormalizeBaselineContentSize(contentSize, target);
        var baseRect = ComputeStandardCenteredRect(baseline, target);

        var scaleFactor = Math.Clamp(alignment.ScalePercent, 50, 150) / 100f;
        var w = baseRect.Width * scaleFactor;
        var h = baseRect.Height * scaleFactor;
        var cx = baseRect.X + baseRect.Width / 2f;
        var cy = baseRect.Y + baseRect.Height / 2f;

        return new RectangleF(
            cx - w / 2f + alignment.OffsetXHundredths,
            cy - h / 2f + alignment.OffsetYHundredths,
            w,
            h);
    }

    /// <summary>If metadata implied a size larger than the page, fit inside at 100% before alignment tweaks.</summary>
    internal static SizeF NormalizeBaselineContentSize(SizeF contentSize, RectangleF target)
    {
        if (contentSize.Width <= target.Width * 1.02f && contentSize.Height <= target.Height * 1.02f)
            return contentSize;

        var scale = Math.Min(target.Width / contentSize.Width, target.Height / contentSize.Height);
        return new SizeF(contentSize.Width * scale, contentSize.Height * scale);
    }

    internal static RectangleF ComputeStandardCenteredRect(SizeF contentSize, RectangleF target)
    {
        if (contentSize.Width <= 0 || contentSize.Height <= 0 || target.Width <= 0 || target.Height <= 0)
            return target;

        var x = target.X + (target.Width - contentSize.Width) / 2f;
        var y = target.Y + (target.Height - contentSize.Height) / 2f;
        return new RectangleF(x, y, contentSize.Width, contentSize.Height);
    }

    internal static RectangleF GetDefaultPaperTarget(bool portrait) =>
        portrait ? new RectangleF(0, 0, 400, 600) : new RectangleF(0, 0, 600, 400);

    internal static SizeF ResolveContentSizeForPreview(string? imagePath, string? layoutId)
    {
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            using var image = Image.FromFile(imagePath);
            return ResolveContentSizeDisplayUnits(image, layoutId).Size;
        }

        return new SizeF(400, 600);
    }

    internal static (SizeF Size, float DpiUsed) ResolveContentSizeDisplayUnits(Image image, string? layoutId)
    {
        var dpi = ResolveExportDpi(image, layoutId);
        var size = new SizeF(image.Width / dpi * 100f, image.Height / dpi * 100f);
        if (size.Width <= 0 || size.Height <= 0)
            size = new SizeF(1, 1);
        return (size, dpi);
    }

    /// <summary>
    /// PNGs often embed 72/96 DPI; dslrBooth exports are 300 DPI. Prefer template Resolution + pixel dimensions.
    /// </summary>
    internal static float ResolveExportDpi(Image image, string? layoutId)
    {
        if (!string.IsNullOrWhiteSpace(layoutId)
            && LayoutSlotGuideService.TryLoadTemplateForLayout(layoutId, out var parsed, out _)
            && parsed != null
            && parsed.CanvasWidth > 0
            && parsed.CanvasHeight > 0)
        {
            var templateDpi = Math.Clamp(parsed.ResolutionDpi, 72, 600);
            if (parsed.CanvasWidth == image.Width && parsed.CanvasHeight == image.Height)
                return templateDpi;
        }

        var dpiX = image.HorizontalResolution;
        var dpiY = image.VerticalResolution;
        if (IsPhotoboothDpi(dpiX) && IsPhotoboothDpi(dpiY))
            return (dpiX + dpiY) / 2f;

        return DefaultExportDpi;
    }

    private static bool IsPhotoboothDpi(float dpi) => dpi is >= 150 and <= 600;

    internal static RectangleF ResolvePrintTargetDisplayUnits(PrintPageEventArgs e)
    {
        try
        {
            var pa = e.PageSettings.PrintableArea;
            if (pa.Width > 10 && pa.Height > 10)
                return new RectangleF(pa.X, pa.Y, pa.Width, pa.Height);
        }
        catch
        {
            /* ignore */
        }

        if (e.MarginBounds.Width > 10 && e.MarginBounds.Height > 10)
            return e.MarginBounds;

        if (e.PageBounds.Width > 10 && e.PageBounds.Height > 10)
            return e.PageBounds;

        return new RectangleF(0, 0, 400, 600);
    }
}
