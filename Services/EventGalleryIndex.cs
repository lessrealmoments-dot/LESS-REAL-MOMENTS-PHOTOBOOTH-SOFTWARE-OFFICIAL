using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoothDesktop.Services;

/// <summary>
/// One JSON file per event root so a sharing station / preview app can resolve print + originals without walking every session folder.
/// Paths in each row are relative to the event root directory (same folder as this file).
/// </summary>
public static class EventGalleryIndex
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static string IndexFilePath(string eventRoot) =>
        Path.Combine(eventRoot, "gallery_index.json");

    public static void UpsertSession(string eventRoot, SessionManifest session)
    {
        if (string.IsNullOrWhiteSpace(eventRoot) || !Directory.Exists(eventRoot)) return;

        var gate = Gates.GetOrAdd(Path.GetFullPath(eventRoot), _ => new object());
        lock (gate)
        {
            var path = IndexFilePath(eventRoot);
            GalleryIndexRoot root;
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    root = JsonSerializer.Deserialize<GalleryIndexRoot>(json) ?? NewRoot(session);
                }
                else
                    root = NewRoot(session);
            }
            catch
            {
                root = NewRoot(session);
            }

            root.Sessions ??= [];

            root.SchemaVersion = 1;
            root.EventId = session.EventId;
            root.EventDisplayName = session.EventName;
            root.UpdatedUtc = DateTime.UtcNow;

            var row = GallerySessionRow.FromManifest(session);
            var idx = root.Sessions.FindIndex(s =>
                string.Equals(s.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                root.Sessions[idx] = row;
            else
                root.Sessions.Add(row);

            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(root, opts));
            File.Move(tmp, path, overwrite: true);
        }
    }

    private static GalleryIndexRoot NewRoot(SessionManifest session) => new()
    {
        SchemaVersion = 1,
        EventId = session.EventId,
        EventDisplayName = session.EventName,
        UpdatedUtc = DateTime.UtcNow,
        Sessions = []
    };
}

public sealed class GalleryIndexRoot
{
    public int SchemaVersion { get; set; } = 1;
    public string EventId { get; set; } = "";
    public string EventDisplayName { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public List<GallerySessionRow> Sessions { get; set; } = [];
}

public sealed class GallerySessionRow
{
    public string SessionId { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string LayoutId { get; set; } = "";
    /// <summary>Relative to event root, e.g. <c>prints/abc.png</c>.</summary>
    public string? PrintRelativeToEvent { get; set; }
    /// <summary>Relative to event root, e.g. <c>originals/abc_shot_001.jpg</c>.</summary>
    public List<string> OriginalsRelativeToEvent { get; set; } = [];
    /// <summary>Set when a QR / cloud gallery slot is assigned (optional).</summary>
    public string? ShareGalleryToken { get; set; }
    public string? ShareGalleryBaseUrl { get; set; }
    /// <summary>Canonical session folder under this event: <c>sessions/{sessionId}</c>.</summary>
    public string SessionFolderRelativeToEvent { get; set; } = "";

    public static GallerySessionRow FromManifest(SessionManifest m)
    {
        var sessionRel = Path.Combine("sessions", m.SessionId).Replace('\\', '/');
        return new GallerySessionRow
        {
            SessionId = m.SessionId,
            CreatedUtc = m.CreatedUtc,
            LayoutId = m.LayoutId,
            PrintRelativeToEvent = m.EventPrintRelativePath,
            OriginalsRelativeToEvent = m.EventOriginalRelativePaths?.ToList() ?? [],
            ShareGalleryToken = m.ShareGalleryToken,
            ShareGalleryBaseUrl = m.ShareGalleryBaseUrl,
            SessionFolderRelativeToEvent = sessionRel
        };
    }
}
