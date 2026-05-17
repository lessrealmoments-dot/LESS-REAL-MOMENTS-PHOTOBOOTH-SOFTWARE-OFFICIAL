using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoothDesktop.Services;

public sealed class PrintAlignmentSample
{
    public required string DisplayName { get; init; }
    /// <summary>Null = built-in test pattern.</summary>
    public string? ImagePath { get; init; }
    public string? LayoutId { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public bool IsPortrait => PixelHeight > PixelWidth;
}

public static class PrintAlignmentSampleService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static PrintAlignmentSample TestPatternSample { get; } = new()
    {
        DisplayName = "Built-in test pattern (4×6)",
        ImagePath = null,
        LayoutId = null,
        PixelWidth = 1200,
        PixelHeight = 1800
    };

    /// <summary>Recent session composites under all events, newest first.</summary>
    public static IReadOnlyList<PrintAlignmentSample> FindRecentComposites(int maxCount = 16)
    {
        var list = new List<PrintAlignmentSample> { TestPatternSample };
        var root = LessRealBoothPaths.Root;
        var eventsDir = Path.Combine(root, "events");
        if (!Directory.Exists(eventsDir))
            return list;

        var hits = new List<(DateTime Utc, PrintAlignmentSample Sample)>();
        foreach (var eventDir in Directory.GetDirectories(eventsDir))
        {
            var sessionsDir = Path.Combine(eventDir, "sessions");
            if (!Directory.Exists(sessionsDir)) continue;

            var eventLabel = Path.GetFileName(eventDir);
            foreach (var sessionDir in Directory.GetDirectories(sessionsDir))
            {
                var composite = Path.Combine(sessionDir, "final", "composite.png");
                if (!File.Exists(composite)) continue;

                var layoutId = TryReadLayoutId(sessionDir);
                var (w, h) = TryReadPixelSize(composite);
                var sessionLabel = Path.GetFileName(sessionDir);
                if (sessionLabel.Length > 10)
                    sessionLabel = sessionLabel[..10] + "…";

                hits.Add((File.GetLastWriteTimeUtc(composite), new PrintAlignmentSample
                {
                    DisplayName = $"{eventLabel} · {sessionLabel}",
                    ImagePath = composite,
                    LayoutId = layoutId,
                    PixelWidth = w,
                    PixelHeight = h
                }));
            }
        }

        foreach (var hit in hits.OrderByDescending(h => h.Utc).Take(maxCount))
            list.Add(hit.Sample);

        return list;
    }

    private static string? TryReadLayoutId(string sessionDir)
    {
        try
        {
            var path = Path.Combine(sessionDir, "session.json");
            if (!File.Exists(path)) return null;
            var manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(path), JsonOpts);
            return string.IsNullOrWhiteSpace(manifest?.LayoutId) ? null : manifest.LayoutId;
        }
        catch
        {
            return null;
        }
    }

    private static (int W, int H) TryReadPixelSize(string imagePath)
    {
        try
        {
            using var img = System.Drawing.Image.FromFile(imagePath);
            return (img.Width, img.Height);
        }
        catch
        {
            return (1200, 1800);
        }
    }
}
