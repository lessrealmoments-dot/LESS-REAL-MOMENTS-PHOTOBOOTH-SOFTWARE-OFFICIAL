using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoothDesktop.Services;

/// <summary>Persists total prints used for an event (for limit enforcement).</summary>
public static class EventPrintCounterService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static string StatsPath(string eventRoot) =>
        Path.Combine(eventRoot, "booth_print_stats.json");

    public static int LoadTotalPrints(string eventId, string eventName)
    {
        var root = LessRealBoothPaths.EventRootDirectory(eventId, eventName);
        return LoadTotalPrintsFromRoot(root);
    }

    public static int LoadTotalPrintsFromRoot(string eventRoot)
    {
        try
        {
            var path = StatsPath(eventRoot);
            if (!File.Exists(path)) return 0;
            var row = JsonSerializer.Deserialize<PrintStatsFile>(File.ReadAllText(path), JsonOpts);
            return Math.Max(0, row?.TotalPrints ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    public static void SaveTotalPrints(string eventId, string eventName, int totalPrints)
    {
        var root = LessRealBoothPaths.EventRootDirectory(eventId, eventName);
        Directory.CreateDirectory(root);
        var path = StatsPath(root);
        var json = JsonSerializer.Serialize(new PrintStatsFile { TotalPrints = Math.Max(0, totalPrints) }, JsonOpts);
        File.WriteAllText(path, json);
    }

    private sealed class PrintStatsFile
    {
        public int TotalPrints { get; set; }
    }
}
