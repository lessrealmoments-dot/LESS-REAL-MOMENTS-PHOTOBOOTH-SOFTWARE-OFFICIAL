using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BoothDesktop.Services;

public static class TemplateCompositor
{
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

            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);

            using (var ctx = dv.RenderOpen())
            {
                ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));

                foreach (var layer in template.Layers.OrderBy(l => l.Z).ThenBy(l => l.Sequence))
                {
                    var dest = new Rect(layer.X, layer.Y, layer.W, layer.H);
                    if (layer.IsPhotoSlot)
                    {
                        if (!photoNumberToOriginalPath.TryGetValue(layer.PhotoNumber, out var shotPath)
                            || string.IsNullOrWhiteSpace(shotPath)
                            || !File.Exists(shotPath))
                        {
                            RuntimeLog.Warn("Composite",
                                $"Photo slot {layer.PhotoNumber}: no capture file (expected session original)");
                            continue;
                        }

                        if (!TryLoadBitmap(shotPath, out var guest, out var gErr))
                        {
                            RuntimeLog.Warn("Composite",
                                $"Photo slot {layer.PhotoNumber}: decode failed path={shotPath} err={gErr}");
                            continue;
                        }

                        DrawImageCover(ctx, guest, dest);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(layer.RelativeImagePath)) continue;
                        var abs = LayoutPackService.ResolvePackAssetFile(packRoot, layer.RelativeImagePath);
                        if (abs == null)
                        {
                            RuntimeLog.Warn("Composite",
                                $"static asset missing rel='{layer.RelativeImagePath}' packRoot={packRoot}");
                            continue;
                        }

                        if (!TryLoadBitmap(abs, out var bmp, out var decErr))
                        {
                            RuntimeLog.Warn("Composite",
                                $"static asset decode failed path={abs} err={decErr}");
                            continue;
                        }

                        ctx.DrawImage(bmp, dest);
                    }
                }
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            var dir = Path.GetDirectoryName(outputPngPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var fs = File.Create(outputPngPath);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            enc.Save(fs);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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

    private static bool TryLoadBitmap(string path, out BitmapSource bmp, out string err)
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
