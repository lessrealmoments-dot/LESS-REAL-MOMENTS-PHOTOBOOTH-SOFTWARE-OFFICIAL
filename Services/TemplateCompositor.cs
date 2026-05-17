using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BoothDesktop.Services;

public static class TemplateCompositor
{
    /// <summary>Photo-slot supersample factor. The decoder is asked to produce a bitmap whose max
    /// edge is this many times the slot's max edge so the final resampler has enough information
    /// to produce sharp output without the cost (or RAM) of decoding the native 15 MP JPEG.</summary>
    private const double PhotoSupersample = 2.0;

    /// <summary>Floor for the requested decode edge so very small slots (e.g. 150 px decorative
    /// thumbnails) still get a reasonably sized source.</summary>
    private const int PhotoMinDecodeEdge = 512;

    public static bool TryComposeToPng(ParsedTemplate template, string packRoot,
        IReadOnlyDictionary<int, string> photoNumberToOriginalPath, string outputPngPath, out string error)
    {
        error = "";
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
            var report = new CompositeQualityReport
            {
                OutputPath = outputPngPath,
                CanvasWidth = w,
                CanvasHeight = h,
                TemplateDpi = templateDpi
            };
            CompositeQualityDiagnostics.EvaluateCanvas(report, template);

            // Phase 0 baseline telemetry — wall time and process working set.
            var proc = Process.GetCurrentProcess();
            var stopwatch = Stopwatch.StartNew();
            var wsBeforeMb = proc.WorkingSet64 / (1024 * 1024);

            var orderedLayers = template.Layers.OrderBy(l => l.Z).ThenBy(l => l.Sequence).ToList();

            // Frame overlay = highest-Z non-locked static layer. Loaded full-res so OverlayHoleBounds
            // can scan its alpha map to find the true transparent window per photo slot.
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
                    TryLoadStaticAsset(overlayPath, out frameOverlay, out _);
            }

            // Phase 1: pre-compute per-PhotoNumber supersample decode targets. If a layout uses the
            // same PhotoNumber in multiple slots (DENHAR puts each photo in two strips), we pick the
            // max so a single decode satisfies every appearance of that photo.
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

            // Lazy per-compose cache keyed by PhotoNumber.
            var photoCache = new Dictionary<int, BitmapSource>();
            var nativeSizeCache = new Dictionary<int, (int W, int H)>();

            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);

            using (var ctx = dv.RenderOpen())
            {
                ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));

                foreach (var layer in orderedLayers)
                {
                    var slotRect = new Rect(layer.X, layer.Y, layer.W, layer.H);

                    if (layer.IsPhotoSlot)
                    {
                        if (!photoNumberToOriginalPath.TryGetValue(layer.PhotoNumber, out var shotPath)
                            || string.IsNullOrWhiteSpace(shotPath)
                            || !File.Exists(shotPath))
                        {
                            report.Warnings.Add($"Photo slot {layer.PhotoNumber}: no capture file on disk");
                            RuntimeLog.Warn("Composite",
                                $"Photo slot {layer.PhotoNumber}: no capture file (expected session original)");
                            continue;
                        }

                        if (!photoCache.TryGetValue(layer.PhotoNumber, out var guest))
                        {
                            var targetEdge = photoTargetEdges.GetValueOrDefault(layer.PhotoNumber, PhotoMinDecodeEdge);
                            if (!TryLoadGuestPhoto(shotPath, targetEdge, out guest, out var nativeSize, out var gErr))
                            {
                                report.Warnings.Add($"Photo slot {layer.PhotoNumber}: decode failed ({gErr})");
                                RuntimeLog.Warn("Composite",
                                    $"Photo slot {layer.PhotoNumber}: decode failed path={shotPath} err={gErr}");
                                continue;
                            }

                            photoCache[layer.PhotoNumber] = guest;
                            nativeSizeCache[layer.PhotoNumber] = nativeSize;
                            CompositeQualityDiagnostics.RegisterPhotoSource(report, layer.PhotoNumber, shotPath,
                                guest, nativeSize.W, nativeSize.H, targetEdge);
                        }

                        if (!nativeSizeCache.TryGetValue(layer.PhotoNumber, out var native))
                            native = (guest.PixelWidth, guest.PixelHeight);

                        var dest = slotRect;
                        if (frameOverlay != null
                            && OverlayHoleBounds.TryGetHoleRect(frameOverlay, slotRect, out var holeRect))
                            dest = holeRect;

                        CompositeQualityDiagnostics.RegisterPhotoSlot(report, layer.PhotoNumber,
                            dest.X, dest.Y, dest.Width, dest.Height, guest, templateDpi,
                            native.W, native.H);

                        // Always center-crop (cover) guest photos to the slot, ignoring dslrBooth's
                        // KeepAspect="False" hint. Stretching distorts faces; cover matches the live
                        // capture-guide crop shown to the guest, so what they framed is what prints.
                        DrawImageCover(ctx, guest, dest);
                    }
                    else
                    {
                        if (layer.IsLocked)
                        {
                            RuntimeLog.Info("Composite",
                                $"skip locked designer layer rel='{layer.RelativeImagePath}'");
                            continue;
                        }

                        if (string.IsNullOrEmpty(layer.RelativeImagePath)) continue;
                        var abs = LayoutPackService.ResolvePackAssetFile(packRoot, layer.RelativeImagePath);
                        if (abs == null)
                        {
                            report.Warnings.Add($"Static asset missing: {layer.RelativeImagePath}");
                            RuntimeLog.Warn("Composite",
                                $"static asset missing rel='{layer.RelativeImagePath}' packRoot={packRoot}");
                            continue;
                        }

                        if (!TryLoadStaticAsset(abs, out var bmp, out var decErr))
                        {
                            report.Warnings.Add($"Static asset decode failed: {abs} ({decErr})");
                            RuntimeLog.Warn("Composite",
                                $"static asset decode failed path={abs} err={decErr}");
                            continue;
                        }

                        CompositeQualityDiagnostics.RegisterStaticAsset(report, abs, bmp,
                            layer.X, layer.Y, layer.W, layer.H);

                        if (layer.KeepAspect)
                            DrawImageFit(ctx, bmp, slotRect);
                        else
                            ctx.DrawImage(bmp, slotRect);
                    }
                }
            }

            // IMPORTANT: render at 96 DPI so visual coordinates (drawing units) map 1:1 to pixels.
            // Using a higher dpi here causes WPF to scale the visual by dpi/96, which clips the
            // rendered area to only the top-left fraction of the canvas (regression observed when
            // templateDpi was passed directly: e.g. 300/96 = 3.125x → only ~32% of the layout
            // landed inside the 1200x1800 bitmap). We re-wrap the pixel buffer below to embed the
            // print DPI metadata in the saved PNG without changing the rasterised geometry.
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            var dir = Path.GetDirectoryName(outputPngPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var stride = w * 4;
            var pixelBuffer = new byte[stride * h];
            rtb.CopyPixels(pixelBuffer, stride, 0);
            var outputBitmap = BitmapSource.Create(w, h, templateDpi, templateDpi,
                PixelFormats.Pbgra32, null, pixelBuffer, stride);

            using (var fs = File.Create(outputPngPath))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(outputBitmap));
                enc.Save(fs);
            }

            // Drop big bitmaps before snapshotting RAM so the figure reflects post-compose steady
            // state, not the transient peak we already captured via PeakWorkingSet64.
            photoCache.Clear();
            nativeSizeCache.Clear();

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
            error = ex.Message;
            return false;
        }
    }

    private static void DrawImageFit(DrawingContext dc, BitmapSource src, Rect destRect)
    {
        var sw = src.PixelWidth;
        var sh = src.PixelHeight;
        if (sw <= 0 || sh <= 0) return;

        var scale = Math.Min(destRect.Width / sw, destRect.Height / sh);
        var scaledW = sw * scale;
        var scaledH = sh * scale;
        var ox = destRect.X + (destRect.Width - scaledW) * 0.5;
        var oy = destRect.Y + (destRect.Height - scaledH) * 0.5;
        dc.DrawImage(src, new Rect(ox, oy, scaledW, scaledH));
    }

    private static void DrawImageCover(DrawingContext dc, BitmapSource src, Rect destRect)
    {
        var sw = src.PixelWidth;
        var sh = src.PixelHeight;
        if (sw <= 0 || sh <= 0) return;

        var dw = destRect.Width;
        var dh = destRect.Height;
        var scale = Math.Max(dw / sw, dh / sh);
        var scaledW = sw * scale;
        var scaledH = sh * scale;
        var ox = destRect.X + (dw - scaledW) * 0.5;
        var oy = destRect.Y + (dh - scaledH) * 0.5;

        dc.PushClip(new RectangleGeometry(destRect));
        dc.DrawImage(src, new Rect(ox, oy, scaledW, scaledH));
        dc.Pop();
    }

    /// <summary>Load a guest photo with shrink-on-load. The JPEG decoder picks the closest DCT
    /// scale (1/2, 1/4, 1/8) and refines to the requested edge. A 4752×3168 JPEG drops from ~60 MB
    /// of Pbgra32 RAM to roughly <c>(targetMaxEdge / longerEdge)² × 60 MB</c>.</summary>
    private static bool TryLoadGuestPhoto(string path, int targetMaxEdge,
        out BitmapSource bmp, out (int W, int H) nativeSize, out string err)
    {
        bmp = null!;
        nativeSize = (0, 0);
        err = "";
        try
        {
            // Cheap header peek: no full decode, just JPEG SOFn read.
            int srcW;
            int srcH;
            using (var headerStream = File.OpenRead(path))
            {
                var dec = BitmapDecoder.Create(headerStream,
                    BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreImageCache,
                    BitmapCacheOption.None);
                var frame = dec.Frames[0];
                srcW = frame.PixelWidth;
                srcH = frame.PixelHeight;
            }
            nativeSize = (srcW, srcH);

            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(path, UriKind.Absolute);
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;

            // Set only the longer-edge dim so WPF preserves aspect automatically. Don't ask for
            // anything bigger than the source — that would force a useless upscale during decode.
            if (srcW >= srcH)
            {
                var clamped = Math.Min(targetMaxEdge, srcW);
                if (clamped > 0 && clamped < srcW)
                    b.DecodePixelWidth = clamped;
            }
            else
            {
                var clamped = Math.Min(targetMaxEdge, srcH);
                if (clamped > 0 && clamped < srcH)
                    b.DecodePixelHeight = clamped;
            }

            b.EndInit();
            b.Freeze();
            bmp = b;
            return true;
        }
        catch (Exception ex)
        {
            err = ex.Message;
            return false;
        }
    }

    /// <summary>Load a static asset (overlay, decor) at full resolution. We do NOT shrink-on-load
    /// these — overlays are authored at canvas size and OverlayHoleBounds needs the full alpha map.</summary>
    private static bool TryLoadStaticAsset(string path, out BitmapSource bmp, out string err)
    {
        bmp = null!;
        err = "";
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
        catch (Exception ex)
        {
            err = ex.Message;
            return false;
        }
    }
}
