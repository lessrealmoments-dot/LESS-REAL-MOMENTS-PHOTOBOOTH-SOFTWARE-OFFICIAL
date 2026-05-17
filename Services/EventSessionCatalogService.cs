using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoothDesktop.Models;

namespace BoothDesktop.Services;

public sealed class EventSessionItem
{
    public required string SessionId { get; init; }
    public DateTime CreatedUtc { get; init; }
    public required string SessionFolderAbs { get; init; }
    public string? CompositeAbs { get; init; }
    public string DisplayLabel => CreatedUtc.ToLocalTime().ToString("MMM d, yyyy · h:mm tt");
}

public sealed class EventSessionDetail
{
    public required EventSessionItem Item { get; init; }
    public IReadOnlyList<string> OriginalAbsPaths { get; init; } = [];
    public string? CompositeAbs { get; init; }
}

public static class EventSessionCatalogService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];

    public static string EventRootFor(BoothEventSummary ev) =>
        LessRealBoothPaths.EventRootDirectory(ev.Id, ev.Name);

    public static IReadOnlyList<EventSessionItem> LoadSessions(BoothEventSummary ev)
    {
        var eventRoot = EventRootFor(ev);
        if (!Directory.Exists(eventRoot))
            return [];

        var byId = new Dictionary<string, EventSessionItem>(StringComparer.OrdinalIgnoreCase);

        TryLoadFromGalleryIndex(eventRoot, byId);
        ScanSessionFolders(eventRoot, byId);

        return byId.Values
            .OrderByDescending(s => s.CreatedUtc)
            .ToList();
    }

    public static EventSessionDetail LoadDetail(EventSessionItem item)
    {
        var originalsDir = Path.Combine(item.SessionFolderAbs, "originals");
        var originals = new List<string>();
        if (Directory.Exists(originalsDir))
        {
            foreach (var f in Directory.GetFiles(originalsDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (IsImageFile(f))
                    originals.Add(f);
            }
        }

        var composite = ResolveCompositePath(item.SessionFolderAbs, item.CompositeAbs);

        return new EventSessionDetail
        {
            Item = new EventSessionItem
            {
                SessionId = item.SessionId,
                CreatedUtc = item.CreatedUtc,
                SessionFolderAbs = item.SessionFolderAbs,
                CompositeAbs = composite
            },
            OriginalAbsPaths = originals,
            CompositeAbs = composite
        };
    }

    private static void TryLoadFromGalleryIndex(string eventRoot,
        Dictionary<string, EventSessionItem> byId)
    {
        var indexPath = EventGalleryIndex.IndexFilePath(eventRoot);
        if (!File.Exists(indexPath)) return;

        try
        {
            var root = JsonSerializer.Deserialize<GalleryIndexRoot>(File.ReadAllText(indexPath), JsonOpts);
            if (root?.Sessions == null) return;

            foreach (var row in root.Sessions)
            {
                if (string.IsNullOrWhiteSpace(row.SessionId)) continue;

                var sessionFolder = Path.Combine(eventRoot, "sessions", row.SessionId);
                if (!Directory.Exists(sessionFolder)) continue;

                var composite = ResolveCompositeFromRow(eventRoot, sessionFolder, row);
                byId[row.SessionId] = new EventSessionItem
                {
                    SessionId = row.SessionId,
                    CreatedUtc = row.CreatedUtc == default ? Directory.GetCreationTimeUtc(sessionFolder) : row.CreatedUtc,
                    SessionFolderAbs = sessionFolder,
                    CompositeAbs = composite
                };
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Gallery", $"gallery_index read failed: {ex.Message}");
        }
    }

    private static void ScanSessionFolders(string eventRoot, Dictionary<string, EventSessionItem> byId)
    {
        var sessionsRoot = Path.Combine(eventRoot, "sessions");
        if (!Directory.Exists(sessionsRoot)) return;

        foreach (var dir in Directory.GetDirectories(sessionsRoot))
        {
            var sessionId = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(sessionId)) continue;

            if (byId.ContainsKey(sessionId)) continue;

            DateTime created;
            try
            {
                created = Directory.GetCreationTimeUtc(dir);
            }
            catch
            {
                created = DateTime.UtcNow;
            }

            byId[sessionId] = new EventSessionItem
            {
                SessionId = sessionId,
                CreatedUtc = created,
                SessionFolderAbs = dir,
                CompositeAbs = ResolveCompositePath(dir, null)
            };
        }
    }

    private static string? ResolveCompositeFromRow(string eventRoot, string sessionFolder, GallerySessionRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.PrintRelativeToEvent))
        {
            var flat = Path.Combine(eventRoot, row.PrintRelativeToEvent.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(flat)) return flat;
        }

        return ResolveCompositePath(sessionFolder, null);
    }

    private static string? ResolveCompositePath(string sessionFolderAbs, string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint) && File.Exists(hint))
            return hint;

        var composite = Path.Combine(sessionFolderAbs, "final", "composite.png");
        if (File.Exists(composite)) return composite;

        try
        {
            var manifestPath = Path.Combine(sessionFolderAbs, "session.json");
            if (!File.Exists(manifestPath)) return null;
            var manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(manifestPath), JsonOpts);
            if (string.IsNullOrWhiteSpace(manifest?.FinalCompositeRelativePath)) return null;
            var rel = manifest.FinalCompositeRelativePath.Replace('/', Path.DirectorySeparatorChar);
            var fromManifest = Path.Combine(sessionFolderAbs, rel);
            return File.Exists(fromManifest) ? fromManifest : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }
}
