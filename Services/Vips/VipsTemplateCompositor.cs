using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using NetVips;

namespace BoothDesktop.Services.Vips;

/// <summary>
/// libvips/NetVips compositor ù drop-in replacement for <see cref="TemplateCompositor"/>.
///
/// Uses <see cref="Image.Thumbnail(string, int, int?, NetVips.Enums.Size?, bool?, NetVips.Enums.Interesting?, bool?, string?, string?, NetVips.Enums.Intent?, NetVips.Enums.FailOn?)"/>
/// for guest photos so each slot benefits from:
///   * JPEG shrink-on-load (libjpeg DCT-level decode at 1/2, 1/4, 1/8 ù same trick dslrBooth uses),
///   * Lanczos3 resampling for the final refinement step (sharper than WPF Fant at &gt;4ù downscales),
///   * streaming / tile-based memory model (~tens of MB instead of decoding the full 15 MP bitmap).
///
/// OverlayHoleBounds still uses a WPF BitmapImage load for the alpha scan ù this is a single,
/// canvas-sized load per composite and is cheap. Phase 3+ can port it to libvips so the Vips
/// path becomes fully WPF-free.
/// </summary>
public static class VipsTemplateCompositor
{
    private const string LogSource = "VipsCompositor";

    /// <summary>Decode-time supersample factor relative to the largest slot edge. Matches the WPF
    /// path's PhotoSupersample so Lanczos3 has the same source headroom regardless of engine.</summary>
    private const double PhotoSupersample = 2.0;

    /// <summary>Floor for the requested decode edge.</summary>
    private const int PhotoMinDecodeEdge = 512;

    private static readonly Lazy<bool> _available = new(() =>
    {
        try
        {
            ModuleInitializer.Initialize();
            using var probe = Image.Black(1, 1);
            var ok = probe.Width == 1 && probe.Height == 1;
            if (ok)
                RuntimeLog.Info(LogSource, $"libvips ready version={NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}");
            return ok;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogSource, $"libvips probe failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    });

    /// <summary>True when the native libvips binaries loaded successfully on this machine.</summary>
    public static bool IsAvailable => _available.Value;

    public static bool TryComposeToPng(ParsedTemplate template, string packRoot,
        IReadOnlyDictionary<int, string> photoNumberToOriginalPath, string outputPngPath, out string error)
    {
        error = "";
        if (!IsAvailable)
        {
            error = "libvips native binaries not available";
            return false;
        }

        try
        {
            var w = template.CanvasWidth;
            var h = template.CanvasHeight;
            if (w <= 0 || h <= 0)
            {
                error = "Bad canvas dimensions.";
                return false;
            }

            var templateDpi = Math.Clamp(template.ResolutionDpi, 72, 600);
            // PNG pHYs metadata is stored in pixels-per-mm. 300 DPI = 300 / 25.4 ? 11.811 ppmm.
            var pixelsPerMm = templateDpi / 25.4;

            var report = new CompositeQualityReport
            {
                OutputPath = outputPngPath,
                CanvasWidth = w,
                CanvasHeight = h,
                TemplateDpi = templateDpi
            };
            CompositeQualityDiagnostics.EvaluateCanvas(report, template);

            var proc = Process.GetCurrentProcess();
            var stopwatch = Stopwatch.StartNew();
            var wsBeforeMb = proc.WorkingSet64 / (1024 * 1024);

            var orderedLayers = template.Layers.OrderBy(l => l.Z).ThenBy(l => l.Sequence).ToList();

            // Overlay alpha-hole detection: keep WPF here for parity with the WPF compositor's
            // proven behaviour. One cheap load per composite.
            BitmapSource? frameOverlay = null;
            var overlayLayer = orderedLayers
                .Where(l => !l.IsPhotoSlot && !l.IsLocked && !string.IsNullOrEmpty(l.RelativeImagePath))
                .OrderByDescending(l => l.Z)
                .ThenBy(l => l.Sequence)
                .FirstOrDefault();
            if (overlayLayer != null)
            {
                var overlayPath = LayoutPackService.ResolvePackAssetFile(packRoot, overlayLayer.RelativeImagePath!);
                if (overlayPath != null)
                    TryLoadBitmapSourceForHoleScan(overlayPath, out frameOverlay);
            }

            // White opaque RGBA canvas. NewFromImage with a 4-double constant gives a 4-band image
            // (R,G,B,A) sized wùh with each pixel set to (255,255,255,255).
            // Tag canvas as sRGB: Composite2 fails with "no known route from 'multiband' to 'srgb'"
            // if the canvas has no interpretation but the overlay does.
            var canvas = Image.Black(w, h)
                .NewFromImage(new double[] { 255, 255, 255, 255 })
                .Cast(NetVips.Enums.BandFormat.Uchar)
                .Copy(interpretation: NetVips.Enums.Interpretation.Srgb);

            // Pre-compute per-PhotoNumber supersample decode targets (matches the WPF path).
            // When the same PhotoNumber appears in multiple slots, we decode at the largest needed
            // supersample edge once, then re-thumbnail per slot from the cached in-memory image.
            var photoTargetEdges = new Dictionary<int, int>();
            foreach (var layer in orderedLayers)
            {
                if (!layer.IsPhotoSlot) continue;
                var maxSlotEdge = Math.Max(layer.W, layer.H);
                var target = (int)Math.Ceiling(maxSlotEdge * PhotoSupersample);
                if (target < PhotoMinDecodeEdge) target = PhotoMinDecodeEdge;
                if (!photoTargetEdges.TryGetValue(layer.PhotoNumber, out var current) || target > current)
                    photoTargetEdges[layer.PhotoNumber] = target;
            }

            var photoCache = new Dictionary<int, CachedPhoto>();

            try
            {
                foreach (var layer in orderedLayers)
                {
                    var slotRect = new Rect(layer.X, layer.Y, layer.W, layer.H);

                    if (layer.IsPhotoSlot)
                    {
                        canvas = DrawGuestPhoto(canvas, layer, slotRect, frameOverlay,
                            photoNumberToOriginalPath, photoTargetEdges, photoCache,
                            templateDpi, report);
                    }
                    else
                    {
                        canvas = DrawStaticLayer(canvas, layer, slotRect, packRoot, report);
                    }
                }

                canvas = canvas.Copy(xres: pixelsPerMm, yres: pixelsPerMm);

                var dir = Path.GetDirectoryName(outputPngPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // Pngsave honours xres/yres from image metadata for the pHYs chunk.
                canvas.Pngsave(outputPngPath, compression: 6);
            }
            finally
            {
                foreach (var cached in photoCache.Values) cached.Working.Dispose();
                photoCache.Clear();
                canvas.Dispose();
            }

            stopwatch.Stop();
            proc.Refresh();
            report.ComposeMs = stopwatch.ElapsedMilliseconds;
            report.WorkingSetBeforeMb = wsBeforeMb;
            report.WorkingSetAfterMb = proc.WorkingSet64 / (1024 * 1024);
            report.PeakWorkingSetMb = proc.PeakWorkingSet64 / (1024 * 1024);

            CompositeQualityDiagnostics.ReadEmbeddedDpi(outputPngPath, report);
            CompositeQualityDiagnostics.WriteLog(report);

            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static Image DrawGuestPhoto(Image canvas, TemplateLayer layer, Rect slotRect,
        BitmapSource? frameOverlay, IReadOnlyDictionary<int, string> photoMap,
        IReadOnlyDictionary<int, int> photoTargetEdges, Dictionary<int, CachedPhoto> photoCache,
        int templateDpi, CompositeQualityReport report)
    {
        if (!photoMap.TryGetValue(layer.PhotoNumber, out var shotPath)
            || string.IsNullOrWhiteSpace(shotPath)
            || !File.Exists(shotPath))
        {
            report.Warnings.Add($"Photo slot {layer.PhotoNumber}: no capture file on disk");
            RuntimeLog.Warn(LogSource,
                $"Photo slot {layer.PhotoNumber}: no capture file (expected session original)");
            return canvas;
        }

        var dest = slotRect;
        if (frameOverlay != null
            && OverlayHoleBounds.TryGetHoleRect(frameOverlay, slotRect, out var holeRect))
            dest = holeRect;

        var destX = (int)Math.Round(dest.X);
        var destY = (int)Math.Round(dest.Y);
        var destW = (int)Math.Round(dest.Width);
        var destH = (int)Math.Round(dest.Height);
        if (destW <= 0 || destH <= 0) return canvas;

        if (!photoCache.TryGetValue(layer.PhotoNumber, out var cached))
        {
            if (!TryLoadGuestPhoto(shotPath,
                    photoTargetEdges.TryGetValue(layer.PhotoNumber, out var edge) ? edge : PhotoMinDecodeEdge,
                    out cached, out var loadErr))
            {
                report.Warnings.Add($"Photo slot {layer.PhotoNumber}: vips decode failed ({loadErr})");
                RuntimeLog.Warn(LogSource,
                    $"Photo slot {layer.PhotoNumber}: decode failed path={shotPath} err={loadErr}");
                return canvas;
            }
            photoCache[layer.PhotoNumber] = cached;
            CompositeQualityDiagnostics.RegisterPhotoSource(report, layer.PhotoNumber, shotPath,
                cached.Working.Width, cached.Working.Height, cached.NativeW, cached.NativeH,
                photoTargetEdges.TryGetValue(layer.PhotoNumber, out var reg) ? reg : PhotoMinDecodeEdge);
        }

        Image thumb;
        try
        {
            // ThumbnailImage operates on the already-decoded working image. Lanczos3 + centre crop.
            thumb = cached.Working.ThumbnailImage(destW, height: destH,
                crop: NetVips.Enums.Interesting.Centre);
        }
        catch (Exception ex)
        {
            report.Warnings.Add($"Photo slot {layer.PhotoNumber}: vips thumbnail failed ({ex.Message})");
            RuntimeLog.Warn(LogSource,
                $"Photo slot {layer.PhotoNumber}: thumbnail failed path={shotPath} err={ex.Message}");
            return canvas;
        }

        var thumbW = thumb.Width;
        var thumbH = thumb.Height;
        var thumbRgba = thumb.HasAlpha() ? thumb : thumb.Bandjoin(255);

        Image newCanvas;
        try
        {
            newCanvas = canvas.Composite2(thumbRgba, NetVips.Enums.BlendMode.Over, x: destX, y: destY);
        }
        finally
        {
            if (!ReferenceEquals(thumb, thumbRgba)) thumbRgba.Dispose();
            thumb.Dispose();
        }

        CompositeQualityDiagnostics.RegisterPhotoSlot(report, layer.PhotoNumber,
            dest.X, dest.Y, dest.Width, dest.Height,
            thumbW, thumbH, templateDpi, cached.NativeW, cached.NativeH);

        canvas.Dispose();
        return newCanvas;
    }

    /// <summary>Decode a guest JPEG once at the supersample edge, then materialise into a memory
    /// image so subsequent <see cref="Image.ThumbnailImage"/> calls (one per slot) avoid re-reading
    /// the file. Matches the WPF compositor's per-compose photo cache.</summary>
    private static bool TryLoadGuestPhoto(string path, int targetMaxEdge,
        out CachedPhoto cached, out string err)
    {
        cached = default!;
        err = "";
        try
        {
            int nativeW, nativeH;
            using (var probe = Image.NewFromFile(path, access: NetVips.Enums.Access.Sequential))
            {
                nativeW = probe.Width;
                nativeH = probe.Height;
            }

            // Image.Thumbnail with Size.Down + no crop = fit-within at the supersample edge.
            // For square-ish layouts the result is approximately targetMaxEdge ◊ targetMaxEdge*aspect.
            // We do NOT pass crop here so the cached image keeps its native aspect ó the per-slot
            // ThumbnailImage below applies the centre crop for each slot's specific aspect ratio.
            using var thumb = Image.Thumbnail(path, targetMaxEdge, height: targetMaxEdge,
                size: NetVips.Enums.Size.Down);

            // Materialise to a memory-backed image so it can be read multiple times (default Vips
            // pipelines are pull-based / single-read).
            var working = thumb.Copy().Cast(NetVips.Enums.BandFormat.Uchar);
            var bytes = working.WriteToMemory();
            var mem = Image.NewFromMemory(bytes, working.Width, working.Height,
                working.Bands, working.Format);

            cached = new CachedPhoto(mem, nativeW, nativeH);
            return true;
        }
        catch (Exception ex)
        {
            err = ex.Message;
            return false;
        }
    }

    /// <summary>Materialised guest photo cached per PhotoNumber for the duration of a single compose.</summary>
    private sealed record CachedPhoto(Image Working, int NativeW, int NativeH);

    private static Image DrawStaticLayer(Image canvas, TemplateLayer layer, Rect slotRect,
        string packRoot, CompositeQualityReport report)
    {
        if (layer.IsLocked)
        {
            RuntimeLog.Info(LogSource,
                $"skip locked designer layer rel='{layer.RelativeImagePath}'");
            return canvas;
        }

        if (string.IsNullOrEmpty(layer.RelativeImagePath)) return canvas;
        var abs = LayoutPackService.ResolvePackAssetFile(packRoot, layer.RelativeImagePath);
        if (abs == null)
        {
            report.Warnings.Add($"Static asset missing: {layer.RelativeImagePath}");
            RuntimeLog.Warn(LogSource,
                $"static asset missing rel='{layer.RelativeImagePath}' packRoot={packRoot}");
            return canvas;
        }

        Image img;
        try
        {
            img = Image.NewFromFile(abs, access: NetVips.Enums.Access.Sequential);
        }
        catch (Exception ex)
        {
            report.Warnings.Add($"Static asset decode failed: {abs} ({ex.Message})");
            RuntimeLog.Warn(LogSource,
                $"static asset decode failed path={abs} err={ex.Message}");
            return canvas;
        }

        var nativeW = img.Width;
        var nativeH = img.Height;

        CompositeQualityDiagnostics.RegisterStaticAsset(report, abs, nativeW, nativeH,
            layer.X, layer.Y, layer.W, layer.H);

        var slotX = (int)Math.Round(slotRect.X);
        var slotY = (int)Math.Round(slotRect.Y);
        var slotW = (int)Math.Round(slotRect.Width);
        var slotH = (int)Math.Round(slotRect.Height);
        if (slotW <= 0 || slotH <= 0)
        {
            img.Dispose();
            return canvas;
        }

        Image scaled;
        int placeX;
        int placeY;

        try
        {
            if (layer.KeepAspect)
            {
                // Fit inside the slot, centered.
                var fitScale = Math.Min((double)slotW / nativeW, (double)slotH / nativeH);
                var fitW = Math.Max(1, (int)Math.Round(nativeW * fitScale));
                var fitH = Math.Max(1, (int)Math.Round(nativeH * fitScale));
                scaled = img.ThumbnailImage(fitW, height: fitH, size: NetVips.Enums.Size.Force);
                placeX = slotX + (slotW - fitW) / 2;
                placeY = slotY + (slotH - fitH) / 2;
            }
            else
            {
                // Stretch to exact slot rect.
                scaled = img.ThumbnailImage(slotW, height: slotH, size: NetVips.Enums.Size.Force);
                placeX = slotX;
                placeY = slotY;
            }
        }
        finally
        {
            img.Dispose();
        }

        var scaledRgba = scaled.HasAlpha() ? scaled : scaled.Bandjoin(255);
        Image newCanvas;
        try
        {
            newCanvas = canvas.Composite2(scaledRgba, NetVips.Enums.BlendMode.Over, x: placeX, y: placeY);
        }
        finally
        {
            if (!ReferenceEquals(scaled, scaledRgba)) scaledRgba.Dispose();
            scaled.Dispose();
        }

        canvas.Dispose();
        return newCanvas;
    }

    private static bool TryLoadBitmapSourceForHoleScan(string path, out BitmapSource? bmp)
    {
        bmp = null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(path, UriKind.Absolute);
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            b.EndInit();
            b.Freeze();
            bmp = b;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
