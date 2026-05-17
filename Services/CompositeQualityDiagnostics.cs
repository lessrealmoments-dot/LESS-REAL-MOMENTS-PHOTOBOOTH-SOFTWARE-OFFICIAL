using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace BoothDesktop.Services;

internal enum CompositeSourceKind
{
    Originals,
    Thumbnail,
    LivePreview,
    Unknown
}

internal sealed class CompositeQualityReport
{
    public string OutputPath { get; init; } = "";
    public int CanvasWidth { get; init; }
    public int CanvasHeight { get; init; }
    public int TemplateDpi { get; init; }
    public double? EmbeddedDpiX { get; set; }
    public double? EmbeddedDpiY { get; set; }
    public long? OutputFileBytes { get; set; }
    public Dictionary<int, PhotoSourceRecord> PhotoSources { get; } = new();
    public List<PhotoSlotRecord> PhotoSlots { get; } = [];
    public List<StaticAssetRecord> StaticAssets { get; } = [];
    public List<string> Warnings { get; } = [];

    // Phase 0 baseline telemetry ù surface what we're trying to beat in later phases.
    public long? ComposeMs { get; set; }
    public long? WorkingSetBeforeMb { get; set; }
    public long? WorkingSetAfterMb { get; set; }
    public long? PeakWorkingSetMb { get; set; }
}

internal sealed class PhotoSourceRecord
{
    public required int PhotoNumber { get; init; }
    public required string Path { get; init; }
    public required CompositeSourceKind Kind { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public long FileBytes { get; init; }
    public int NativePixelWidth { get; init; }
    public int NativePixelHeight { get; init; }
    public int DecodeRequestedMaxEdge { get; init; }
}

internal sealed class PhotoSlotRecord
{
    public required int PhotoNumber { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double CoverScale { get; init; }
    public int SourcePixelWidth { get; init; }
    public int SourcePixelHeight { get; init; }
    public bool HeavyDownscale { get; init; }
    public bool SlotTooSmall { get; init; }
    public bool SourceUpscaled { get; init; }

    // Phase 1 ù the bitmap actually fed to DrawImageCover after shrink-on-load.
    // Equals Source* when the decoder ignored DecodePixel hints.
    public int EffectiveSourcePixelWidth { get; init; }
    public int EffectiveSourcePixelHeight { get; init; }
}

internal sealed class StaticAssetRecord
{
    public required string Path { get; init; }
    public int NativeWidth { get; init; }
    public int NativeHeight { get; init; }
    public double DestX { get; init; }
    public double DestY { get; init; }
    public double DestWidth { get; init; }
    public double DestHeight { get; init; }
    public double ScaleX { get; init; }
    public double ScaleY { get; init; }
    public bool Upscaled { get; init; }
}

/// <summary>Structured quality report for final/composite.png (Phase A diagnostics).</summary>
internal static class CompositeQualityDiagnostics
{
    private const string LogSource = "CompositeQuality";
    private const double HeavyDownscaleCoverScale = 0.2;
    private const double SourceUpscaleCoverScale = 1.05;
    private const double StaticUpscaleFactor = 1.05;
    private const double MinSlotEdgeInches = 0.8;

    public static CompositeSourceKind ClassifySourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return CompositeSourceKind.Unknown;

        var norm = path.Replace('/', Path.DirectorySeparatorChar);
        if (norm.Contains($"{Path.DirectorySeparatorChar}originals{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            return CompositeSourceKind.Originals;

        if (norm.Contains($"{Path.DirectorySeparatorChar}thumbs{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
            || norm.Contains($"{Path.DirectorySeparatorChar}final{Path.DirectorySeparatorChar}thumbs",
                StringComparison.OrdinalIgnoreCase))
            return CompositeSourceKind.Thumbnail;

        if (norm.Contains("live.jpg", StringComparison.OrdinalIgnoreCase)
            || norm.Contains($"{Path.DirectorySeparatorChar}live{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            return CompositeSourceKind.LivePreview;

        return CompositeSourceKind.Unknown;
    }

    public static void RegisterPhotoSource(CompositeQualityReport report, int photoNumber, string path,
        BitmapSource bitmap, int nativePixelWidth = 0, int nativePixelHeight = 0,
        int decodeRequestedMaxEdge = 0)
        => RegisterPhotoSource(report, photoNumber, path,
            bitmap.PixelWidth, bitmap.PixelHeight,
            nativePixelWidth, nativePixelHeight, decodeRequestedMaxEdge);

    /// <summary>Primitive overload used by non-WPF compositors (libvips).</summary>
    public static void RegisterPhotoSource(CompositeQualityReport report, int photoNumber, string path,
        int effectivePixelWidth, int effectivePixelHeight,
        int nativePixelWidth = 0, int nativePixelHeight = 0,
        int decodeRequestedMaxEdge = 0)
    {
        if (report.PhotoSources.ContainsKey(photoNumber))
            return;

        long bytes = 0;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch
        {
            /* ignore */
        }

        var kind = ClassifySourcePath(path);
        report.PhotoSources[photoNumber] = new PhotoSourceRecord
        {
            PhotoNumber = photoNumber,
            Path = path,
            Kind = kind,
            PixelWidth = effectivePixelWidth,
            PixelHeight = effectivePixelHeight,
            FileBytes = bytes,
            NativePixelWidth = nativePixelWidth > 0 ? nativePixelWidth : effectivePixelWidth,
            NativePixelHeight = nativePixelHeight > 0 ? nativePixelHeight : effectivePixelHeight,
            DecodeRequestedMaxEdge = decodeRequestedMaxEdge
        };

        if (kind != CompositeSourceKind.Originals)
        {
            report.Warnings.Add(
                $"Photo {photoNumber} source is '{kind}' not session originals ù path={path}");
        }
    }

    public static void RegisterPhotoSlot(CompositeQualityReport report, int photoNumber,
        double x, double y, double w, double h, BitmapSource source, int templateDpi,
        int nativeSourceWidth = 0, int nativeSourceHeight = 0)
        => RegisterPhotoSlot(report, photoNumber, x, y, w, h,
            source.PixelWidth, source.PixelHeight, templateDpi,
            nativeSourceWidth, nativeSourceHeight);

    /// <summary>Primitive overload used by non-WPF compositors (libvips).</summary>
    public static void RegisterPhotoSlot(CompositeQualityReport report, int photoNumber,
        double x, double y, double w, double h,
        int effectiveSourceWidth, int effectiveSourceHeight, int templateDpi,
        int nativeSourceWidth = 0, int nativeSourceHeight = 0)
    {
        // CoverScale is measured against the NATIVE source (pre-shrink-on-load) so the
        // metric stays comparable across phases ù shrink-on-load shouldn't change the
        // perceived "this slot heavily downscales the original" warning.
        var nw = nativeSourceWidth > 0 ? nativeSourceWidth : effectiveSourceWidth;
        var nh = nativeSourceHeight > 0 ? nativeSourceHeight : effectiveSourceHeight;
        var metrics = ComputeCoverMetrics(nw, nh, w, h);
        var heavyDownscale = metrics.CoverScale < HeavyDownscaleCoverScale && metrics.SourceLargerThanSlot;
        var sourceUpscaled = metrics.CoverScale > SourceUpscaleCoverScale;
        var slotTooSmall = IsSlotTooSmall(w, h, templateDpi);

        report.PhotoSlots.Add(new PhotoSlotRecord
        {
            PhotoNumber = photoNumber,
            X = x,
            Y = y,
            Width = w,
            Height = h,
            CoverScale = metrics.CoverScale,
            SourcePixelWidth = nw,
            SourcePixelHeight = nh,
            HeavyDownscale = heavyDownscale,
            SlotTooSmall = slotTooSmall,
            SourceUpscaled = sourceUpscaled,
            EffectiveSourcePixelWidth = effectiveSourceWidth,
            EffectiveSourcePixelHeight = effectiveSourceHeight
        });

        if (heavyDownscale)
        {
            report.Warnings.Add(
                $"Photo {photoNumber} slot {w:F0}x{h:F0}px heavily downscales source " +
                $"{nw}x{nh} (coverScale={metrics.CoverScale:F4})");
        }

        if (slotTooSmall)
        {
            report.Warnings.Add(
                $"Photo {photoNumber} slot {w:F0}x{h:F0}px may be too small for {templateDpi} DPI print " +
                $"(min edge {MinSlotEdgeInches:F1} in ? {templateDpi * MinSlotEdgeInches:F0}px)");
        }

        if (sourceUpscaled)
        {
            report.Warnings.Add(
                $"Photo {photoNumber} slot upscales a small source {nw}x{nh} " +
                $"(coverScale={metrics.CoverScale:F4})");
        }
    }

    public static void RegisterStaticAsset(CompositeQualityReport report, string path, BitmapSource bitmap,
        double x, double y, double w, double h)
        => RegisterStaticAsset(report, path, bitmap.PixelWidth, bitmap.PixelHeight, x, y, w, h);

    /// <summary>Primitive overload used by non-WPF compositors (libvips).</summary>
    public static void RegisterStaticAsset(CompositeQualityReport report, string path,
        int nativeWidth, int nativeHeight,
        double x, double y, double w, double h)
    {
        var scaleX = nativeWidth > 0 ? w / nativeWidth : 0;
        var scaleY = nativeHeight > 0 ? h / nativeHeight : 0;
        var upscaled = scaleX > StaticUpscaleFactor || scaleY > StaticUpscaleFactor;

        report.StaticAssets.Add(new StaticAssetRecord
        {
            Path = path,
            NativeWidth = nativeWidth,
            NativeHeight = nativeHeight,
            DestX = x,
            DestY = y,
            DestWidth = w,
            DestHeight = h,
            ScaleX = scaleX,
            ScaleY = scaleY,
            Upscaled = upscaled
        });

        if (upscaled)
        {
            report.Warnings.Add(
                $"Static asset upscaled native {nativeWidth}x{nativeHeight} dest {w:F0}x{h:F0} " +
                $"(scaleX={scaleX:F2} scaleY={scaleY:F2}) path={path}");
        }
    }

    public static void EvaluateCanvas(CompositeQualityReport report, ParsedTemplate template)
    {
        var dpi = template.ResolutionDpi;
        var expected4x6Portrait = (w: dpi * 4, h: dpi * 6);
        var expected4x6Landscape = (w: dpi * 6, h: dpi * 4);
        var cw = template.CanvasWidth;
        var ch = template.CanvasHeight;

        var matchesPortrait = cw == expected4x6Portrait.w && ch == expected4x6Portrait.h;
        var matchesLandscape = cw == expected4x6Landscape.w && ch == expected4x6Landscape.h;
        if (!matchesPortrait && !matchesLandscape)
        {
            report.Warnings.Add(
                $"Canvas {cw}x{ch}px is not standard 4x6 at {dpi} DPI " +
                $"(expected {expected4x6Portrait.w}x{expected4x6Portrait.h} portrait or " +
                $"{expected4x6Landscape.w}x{expected4x6Landscape.h} landscape) ù print scaling risk");
        }
    }

    public static void ReadEmbeddedDpi(string pngPath, CompositeQualityReport report)
    {
        try
        {
            using var stream = File.OpenRead(pngPath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            report.EmbeddedDpiX = frame.DpiX;
            report.EmbeddedDpiY = frame.DpiY;

            var expected = (double)report.TemplateDpi;
            if (Math.Abs(frame.DpiX - expected) > 1 || Math.Abs(frame.DpiY - expected) > 1)
            {
                report.Warnings.Add(
                    $"Embedded PNG DPI {frame.DpiX:F0}x{frame.DpiY:F0} differs from template {report.TemplateDpi}");
            }
        }
        catch (Exception ex)
        {
            report.Warnings.Add($"Could not read embedded PNG DPI: {ex.Message}");
        }
    }

    public static void WriteLog(CompositeQualityReport report)
    {
        try
        {
            report.OutputFileBytes = new FileInfo(report.OutputPath).Length;
        }
        catch
        {
            /* ignore */
        }

        var sb = new StringBuilder();
        sb.AppendLine("composite quality report");
        sb.Append(CultureInfo.InvariantCulture,
            $"  output={report.OutputPath}");
        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture,
            $"  canvas={report.CanvasWidth}x{report.CanvasHeight}px templateDpi={report.TemplateDpi}");
        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture,
            $"  embeddedDpi={FormatDpi(report.EmbeddedDpiX)}x{FormatDpi(report.EmbeddedDpiY)}");
        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture,
            $"  outputBytes={report.OutputFileBytes ?? 0}");

        if (report.ComposeMs.HasValue
            || report.WorkingSetBeforeMb.HasValue
            || report.WorkingSetAfterMb.HasValue
            || report.PeakWorkingSetMb.HasValue)
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture,
                $"  perf composeMs={report.ComposeMs ?? -1} " +
                $"wsBefore={report.WorkingSetBeforeMb ?? -1}MB " +
                $"wsAfter={report.WorkingSetAfterMb ?? -1}MB " +
                $"wsPeak={report.PeakWorkingSetMb ?? -1}MB");
        }

        foreach (var p in report.PhotoSources.Values.OrderBy(p => p.PhotoNumber))
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture,
                $"  photo[{p.PhotoNumber}] source={p.Kind} path={p.Path}");
            sb.AppendLine();
            var shrinkNote = p.DecodeRequestedMaxEdge > 0
                ? $" decodedFromNative={p.NativePixelWidth}x{p.NativePixelHeight} reqMaxEdge={p.DecodeRequestedMaxEdge}"
                : "";
            sb.Append(CultureInfo.InvariantCulture,
                $"    sourcePx={p.PixelWidth}x{p.PixelHeight} fileBytes={p.FileBytes}{shrinkNote}");
        }

        foreach (var slot in report.PhotoSlots)
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture,
                $"  slot photo={slot.PhotoNumber} rect=({slot.X:F0},{slot.Y:F0},{slot.Width:F0}x{slot.Height:F0})");
            sb.AppendLine();
            var effNote =
                slot.EffectiveSourcePixelWidth != slot.SourcePixelWidth
                || slot.EffectiveSourcePixelHeight != slot.SourcePixelHeight
                    ? $" effectivePx={slot.EffectiveSourcePixelWidth}x{slot.EffectiveSourcePixelHeight}"
                    : "";
            sb.Append(CultureInfo.InvariantCulture,
                $"    coverScale={slot.CoverScale:F4} sourcePx={slot.SourcePixelWidth}x{slot.SourcePixelHeight}{effNote} " +
                $"heavyDownscale={slot.HeavyDownscale} slotTooSmall={slot.SlotTooSmall} sourceUpscaled={slot.SourceUpscaled}");
        }

        foreach (var asset in report.StaticAssets)
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture,
                $"  static path={asset.Path}");
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture,
                $"    native={asset.NativeWidth}x{asset.NativeHeight} dest=({asset.DestX:F0},{asset.DestY:F0}," +
                $"{asset.DestWidth:F0}x{asset.DestHeight:F0}) scale=({asset.ScaleX:F2},{asset.ScaleY:F2}) upscaled={asset.Upscaled}");
        }

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  warnings ({report.Warnings.Count}):");
            foreach (var w in report.Warnings)
                sb.AppendLine($"    - {w}");
        }

        RuntimeLog.Info(LogSource, sb.ToString().TrimEnd());
    }

    internal static CoverMetrics ComputeCoverMetrics(int sourceW, int sourceH, double destW, double destH)
    {
        if (sourceW <= 0 || sourceH <= 0 || destW <= 0 || destH <= 0)
            return new CoverMetrics(0, false);

        var scale = Math.Max(destW / sourceW, destH / sourceH);
        var sourceLarger = sourceW > destW * 1.02 || sourceH > destH * 1.02;
        return new CoverMetrics(scale, sourceLarger);
    }

    private static bool IsSlotTooSmall(double w, double h, int templateDpi)
    {
        var minEdgePx = templateDpi * MinSlotEdgeInches;
        return w < minEdgePx || h < minEdgePx || w < 150 || h < 150;
    }

    private static string FormatDpi(double? dpi) =>
        dpi.HasValue ? dpi.Value.ToString("F0", CultureInfo.InvariantCulture) : "?";

    internal readonly record struct CoverMetrics(double CoverScale, bool SourceLargerThanSlot);
}
