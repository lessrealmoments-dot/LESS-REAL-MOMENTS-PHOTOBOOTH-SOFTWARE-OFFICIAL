using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace BoothDesktop.Services;

/// <summary>Builds final/compilation.gif from session originals for the session viewer.</summary>
public static class SessionGifService
{
    private const int FrameDelayCs = 40; // 400ms per frame (hundredths of a second)

    // GDI+ frame delay encoder (not exposed on Encoder in newer .NET).
    private static readonly Encoder FrameDelayEncoder = new(
        new Guid(0x6aedbd6d, 0x3cb0, 0x11d0, 0xb6, 0x89, 0x00, 0xc0, 0x37, 0x66, 0xb6, 0xeb));

    public static string CompilationGifPath(string sessionFolderAbs) =>
        Path.Combine(sessionFolderAbs, "final", "compilation.gif");

    /// <returns>Path to GIF if available or built; null if no originals.</returns>
    public static string? EnsureCompilationGif(string sessionFolderAbs, IReadOnlyList<string> originalAbsPaths)
    {
        var outPath = CompilationGifPath(sessionFolderAbs);
        if (File.Exists(outPath))
            return outPath;

        if (originalAbsPaths.Count == 0)
            return null;

        try
        {
            var finalDir = Path.GetDirectoryName(outPath)!;
            Directory.CreateDirectory(finalDir);

            if (originalAbsPaths.Count == 1)
            {
                File.Copy(originalAbsPaths[0], outPath, overwrite: true);
                return outPath;
            }

            if (TryBuildAnimatedGif(originalAbsPaths, outPath))
                return outPath;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Gif", $"compilation.gif failed session={Path.GetFileName(sessionFolderAbs)} err={ex.Message}");
        }

        return null;
    }

    private static bool TryBuildAnimatedGif(IReadOnlyList<string> imagePaths, string outputPath)
    {
        var images = new List<Image>();
        try
        {
            foreach (var path in imagePaths)
            {
                using var loaded = Image.FromFile(path);
                images.Add(new Bitmap(loaded));
            }

            if (images.Count == 0) return false;

            var encoder = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == ImageFormat.Gif.Guid);
            if (encoder == null) return false;

            var delay = new EncoderParameter(FrameDelayEncoder, FrameDelayCs);
            var saveFlag = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
            var encParams = new EncoderParameters(2);
            encParams.Param[0] = saveFlag;
            encParams.Param[1] = delay;

            images[0].Save(outputPath, encoder, encParams);

            saveFlag = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.FrameDimensionTime);
            encParams.Param[0] = saveFlag;
            for (var i = 1; i < images.Count; i++)
                images[0].SaveAdd(images[i], encParams);

            saveFlag = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.Flush);
            encParams.Param[0] = saveFlag;
            images[0].SaveAdd(null!, encParams);

            return File.Exists(outputPath);
        }
        finally
        {
            foreach (var img in images)
                img.Dispose();
        }
    }
}
