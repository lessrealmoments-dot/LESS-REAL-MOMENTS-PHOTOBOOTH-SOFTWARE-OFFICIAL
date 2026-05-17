using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoothDesktop.Services;

/// <summary>Resolves full-quality print path + layout id for a saved session folder.</summary>
public static class SessionPrintResolver
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Resolves <c>final/composite.png</c> only — never session thumbnails.</summary>
    public static bool TryResolveFullQualityPrint(string sessionFolderAbs, out string? printPathAbs,
        out string? layoutId, out string? error) =>
        TryResolve(sessionFolderAbs, out printPathAbs, out layoutId, out error);

    public static bool TryResolve(string sessionFolderAbs, out string? printPathAbs, out string? layoutId, out string? error)
    {
        printPathAbs = null;
        layoutId = null;
        error = null;

        if (string.IsNullOrWhiteSpace(sessionFolderAbs) || !Directory.Exists(sessionFolderAbs))
        {
            error = "Session folder not found.";
            return false;
        }

        var manifestPath = Path.Combine(sessionFolderAbs, "session.json");
        SessionManifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            try
            {
                manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(manifestPath), JsonOpts);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        layoutId = manifest?.LayoutId;

        var composite = Path.Combine(sessionFolderAbs, "final", "composite.png");
        if (File.Exists(composite))
            printPathAbs = composite;
        else if (!string.IsNullOrWhiteSpace(manifest?.FinalCompositeRelativePath))
        {
            var rel = manifest.FinalCompositeRelativePath.Replace('/', Path.DirectorySeparatorChar);
            var fromManifest = Path.Combine(sessionFolderAbs, rel);
            if (File.Exists(fromManifest))
                printPathAbs = fromManifest;
        }

        if (string.IsNullOrEmpty(printPathAbs) || !File.Exists(printPathAbs))
        {
            error = "No composite print file for this session.";
            return false;
        }

        if (IsThumbnailPath(printPathAbs))
        {
            error = "Refusing to print thumbnail; composite missing.";
            printPathAbs = null;
            return false;
        }

        return true;
    }

    private static bool IsThumbnailPath(string path)
    {
        var norm = path.Replace('/', Path.DirectorySeparatorChar);
        return norm.Contains($"{Path.DirectorySeparatorChar}thumbs{Path.DirectorySeparatorChar}",
                   StringComparison.OrdinalIgnoreCase)
               || norm.Contains($"{Path.DirectorySeparatorChar}final{Path.DirectorySeparatorChar}thumbs",
                   StringComparison.OrdinalIgnoreCase);
    }
}
