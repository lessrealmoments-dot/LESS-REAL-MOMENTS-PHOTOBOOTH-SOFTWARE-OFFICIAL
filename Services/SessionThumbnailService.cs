using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace BoothDesktop.Services;

/// <summary>Small JPEG previews for session browsing (full files reserved for print/share).</summary>
public static class SessionThumbnailService
{
    public const int GridPreviewMax = 280;
    public const int DetailOverlayMax = 720;
    public const int DetailGifMax = 320;
    public const int DetailRawMax = 160;

    public static string ThumbsDirectory(string sessionFolderAbs) =>
        Path.Combine(sessionFolderAbs, "final", "thumbs");

    public static string? EnsureCompositeGridThumb(string sessionFolderAbs, string? compositeAbs) =>
        EnsureThumbnail(sessionFolderAbs, compositeAbs, "composite_grid.jpg", GridPreviewMax);

    public static string? EnsureCompositeDetailThumb(string sessionFolderAbs, string? compositeAbs) =>
        EnsureThumbnail(sessionFolderAbs, compositeAbs, "composite_detail.jpg", DetailOverlayMax);

    public static string? EnsureGifPreviewThumb(string sessionFolderAbs, IReadOnlyList<string> originalAbsPaths)
    {
        if (originalAbsPaths.Count == 0) return null;
        var compilation = SessionGifService.CompilationGifPath(sessionFolderAbs);
        var source = File.Exists(compilation) ? compilation : originalAbsPaths[0];
        return EnsureThumbnail(sessionFolderAbs, source, "gif_preview.jpg", DetailGifMax);
    }

    public static string? EnsureRawThumb(string sessionFolderAbs, string originalAbs)
    {
        var name = Path.GetFileNameWithoutExtension(originalAbs) + ".jpg";
        return EnsureThumbnail(sessionFolderAbs, originalAbs, name, DetailRawMax);
    }

    private static string? EnsureThumbnail(string sessionFolderAbs, string? sourceAbs, string thumbFileName, int maxEdge)
    {
        if (string.IsNullOrWhiteSpace(sourceAbs) || !File.Exists(sourceAbs))
            return null;

        var dir = ThumbsDirectory(sessionFolderAbs);
        Directory.CreateDirectory(dir);
        var thumbPath = Path.Combine(dir, thumbFileName);

        try
        {
            if (File.Exists(thumbPath))
            {
                var srcTime = File.GetLastWriteTimeUtc(sourceAbs);
                var thumbTime = File.GetLastWriteTimeUtc(thumbPath);
                if (thumbTime >= srcTime)
                    return thumbPath;
            }

            WriteJpegThumb(sourceAbs, thumbPath, maxEdge);
            return File.Exists(thumbPath) ? thumbPath : null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Thumb", $"thumb failed {thumbFileName} err={ex.Message}");
            return null;
        }
    }

    private static void WriteJpegThumb(string sourceAbs, string thumbPath, int maxEdge)
    {
        using var loaded = Image.FromFile(sourceAbs);
        var (w, h) = FitInside(loaded.Width, loaded.Height, maxEdge);
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(loaded, 0, 0, w, h);
        }

        var jpeg = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (jpeg == null)
        {
            bmp.Save(thumbPath, ImageFormat.Jpeg);
            return;
        }

        using var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(Encoder.Quality, 82L);
        bmp.Save(thumbPath, jpeg, encParams);
    }

    private static (int W, int H) FitInside(int width, int height, int maxEdge)
    {
        if (width <= 0 || height <= 0)
            return (maxEdge, maxEdge);
        var scale = Math.Min((double)maxEdge / width, (double)maxEdge / height);
        if (scale >= 1)
            return (width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}
