using System.IO;

namespace BoothDesktop.Services;

/// <summary>
/// Data-root storage for sessions, originals, finals, and optional layout previews.
/// Default root is Documents/LessRealBooth, but operators can override it in Global settings.
/// </summary>
public static class LessRealBoothPaths
{
    public const string RootFolderName = "LessRealBooth";

    public static string Root
    {
        get
        {
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), RootFolderName);
            var cfg = GlobalSettingsService.Load();
            if (string.IsNullOrWhiteSpace(cfg.StorageRootPath))
                return fallback;
            try
            {
                return Path.GetFullPath(cfg.StorageRootPath.Trim());
            }
            catch
            {
                return fallback;
            }
        }
    }

    /// <summary>Pre-installed or user-dropped preview.png per layout id (e.g. lay_h2).</summary>
    public static string LayoutPreviewDirectory(string layoutId) =>
        Path.Combine(Root, "layout_previews", layoutId);

    public static string LayoutPreviewFile(string layoutId) =>
        Path.Combine(LayoutPreviewDirectory(layoutId), "preview.png");

    public static string EventRootDirectory(string eventId, string eventDisplayName)
    {
        var safe = SanitizeFileSegment(eventDisplayName);
        if (string.IsNullOrWhiteSpace(safe)) safe = "event";
        return Path.Combine(Root, "events", $"{safe}_{SanitizeFileSegment(eventId)}");
    }

    public static string SessionDirectory(string eventId, string eventDisplayName, string sessionId) =>
        Path.Combine(EventRootDirectory(eventId, eventDisplayName), "sessions", sessionId);

    public static string SessionOriginals(string eventId, string eventDisplayName, string sessionId) =>
        Path.Combine(SessionDirectory(eventId, eventDisplayName, sessionId), "originals");

    public static string SessionFinal(string eventId, string eventDisplayName, string sessionId) =>
        Path.Combine(SessionDirectory(eventId, eventDisplayName, sessionId), "final");

    public static string SessionManifestPath(string eventId, string eventDisplayName, string sessionId) =>
        Path.Combine(SessionDirectory(eventId, eventDisplayName, sessionId), "session.json");

    /// <summary>Flat composites for this event (easy ZIP for client soft-copy of prints).</summary>
    public static string EventPrintsDirectory(string eventId, string eventDisplayName) =>
        Path.Combine(EventRootDirectory(eventId, eventDisplayName), "prints");

    /// <summary>Flat originals for this event (mirrors of session captures; same event root as <see cref="EventPrintsDirectory"/>).</summary>
    public static string EventOriginalsDirectory(string eventId, string eventDisplayName) =>
        Path.Combine(EventRootDirectory(eventId, eventDisplayName), "originals");

    /// <summary>Shared media for this event (start screen, pre-roll MP4), referenced from events_registry.json.</summary>
    public static string EventMediaDirectory(string eventId, string eventDisplayName) =>
        Path.Combine(EventRootDirectory(eventId, eventDisplayName), "media");

    /// <summary>Resolves <paramref name="relativePath"/> under the event root (must stay under that root).</summary>
    public static string? TryResolveEventMediaFile(string eventId, string eventDisplayName, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var root = EventRootDirectory(eventId, eventDisplayName);
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (rel.Contains(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || rel.Equals("..", StringComparison.Ordinal))
            return null;
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var rootFull = Path.GetFullPath(root);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>Imported dslrBooth-style layout packs (ZIP extract).</summary>
    public static string CatalogRoot => Path.Combine(Root, "catalog");

    public static string CatalogLayoutsStore => Path.Combine(CatalogRoot, "layouts");

    public static string CatalogLayoutsJson => Path.Combine(CatalogRoot, "layouts_catalog.json");

    /// <summary>Events list + per-event enabled layout ids.</summary>
    public static string EventsRegistryJson => Path.Combine(Root, "events_registry.json");

    public static string LogsRoot => Path.Combine(Root, "logs");

    public static string RuntimeLogPathUtc(DateTime utcNow) =>
        Path.Combine(LogsRoot, $"runtime_{utcNow:yyyyMMdd}.log");

    public static void EnsureLayoutPreviewHintFile()
    {
        try
        {
            var dir = Path.Combine(Root, "layout_previews");
            Directory.CreateDirectory(dir);
            var readme = Path.Combine(dir, "README.txt");
            if (!File.Exists(readme))
            {
                File.WriteAllText(readme,
                    "Drop dslrBooth preview.png files here for each built-in layout id, OR use\r\n" +
                    "  Events → Import layout (ZIP) to add packs under catalog\\layouts\\\r\n");
            }
        }
        catch
        {
            /* ignore */
        }
    }

    public static string SanitizeFileSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars);
        return s.Length > 80 ? s[..80] : s;
    }
}
