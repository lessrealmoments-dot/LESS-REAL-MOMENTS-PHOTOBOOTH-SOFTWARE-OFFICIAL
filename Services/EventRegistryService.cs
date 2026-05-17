using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoothDesktop.Models;

namespace BoothDesktop.Services;

/// <summary>Persists events and per-event layout visibility under Documents/LessRealBooth.</summary>
public static class EventRegistryService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void EnsureRegistryFile()
    {
        try
        {
            Directory.CreateDirectory(LessRealBoothPaths.Root);
            var path = LessRealBoothPaths.EventsRegistryJson;
            if (File.Exists(path)) return;

            var seed = new EventsRegistryFile
            {
                Events =
                [
                    new EventRecord
                    {
                        Id = "evt_demo",
                        Name = "Cleo & Heart — reception",
                        CreatedUtc = DateTime.UtcNow.AddDays(-2),
                        IsArchived = false,
                        EnabledLayoutIds = null
                    },
                    new EventRecord
                    {
                        Id = "evt_sample",
                        Name = "Sample gala",
                        CreatedUtc = DateTime.UtcNow.AddDays(-14),
                        IsArchived = false,
                        EnabledLayoutIds = null
                    }
                ]
            };
            File.WriteAllText(path, JsonSerializer.Serialize(seed, JsonOpts));
        }
        catch
        {
            /* ignore */
        }
    }

    public static List<BoothEventSummary> LoadEvents()
    {
        EnsureRegistryFile();
        try
        {
            var json = File.ReadAllText(LessRealBoothPaths.EventsRegistryJson);
            var file = JsonSerializer.Deserialize<EventsRegistryFile>(json, JsonOpts);
            if (file?.Events == null || file.Events.Count == 0)
                return [];

            return file.Events
                .Where(e => !e.IsArchived)
                .OrderByDescending(e => e.CreatedUtc)
                .Select(e => new BoothEventSummary
                {
                    Id = e.Id,
                    Name = e.Name,
                    CreatedUtc = e.CreatedUtc,
                    IsArchived = e.IsArchived,
                    EnabledLayoutIds = e.EnabledLayoutIds,
                    Experience = CloneExperience(e.Experience)
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static bool TrySaveEnabledLayouts(string eventId, IReadOnlyList<string> layoutIds)
    {
        if (string.IsNullOrWhiteSpace(eventId) || layoutIds == null || layoutIds.Count == 0)
            return false;
        return TrySetEnabledLayouts(eventId, layoutIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>null or empty list = all available layouts visible for the event.</summary>
    public static bool TrySetEnabledLayouts(string eventId, List<string>? layoutIds)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return false;

        try
        {
            EnsureRegistryFile();
            var path = LessRealBoothPaths.EventsRegistryJson;
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<EventsRegistryFile>(json, JsonOpts) ?? new EventsRegistryFile();
            var ev = file.Events.FirstOrDefault(x => string.Equals(x.Id, eventId, StringComparison.OrdinalIgnoreCase));
            if (ev == null)
                return false;

            ev.EnabledLayoutIds = layoutIds is { Count: > 0 }
                ? layoutIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : null;
            File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOpts));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static BoothEventExperienceSettings GetExperience(string eventId)
    {
        EnsureRegistryFile();
        try
        {
            var json = File.ReadAllText(LessRealBoothPaths.EventsRegistryJson);
            var file = JsonSerializer.Deserialize<EventsRegistryFile>(json, JsonOpts);
            var ev = file?.Events.FirstOrDefault(x =>
                string.Equals(x.Id, eventId, StringComparison.OrdinalIgnoreCase));
            return CloneExperience(ev?.Experience);
        }
        catch
        {
            return new BoothEventExperienceSettings();
        }
    }

    public static bool TrySaveExperience(string eventId, BoothEventExperienceSettings experience)
    {
        if (string.IsNullOrWhiteSpace(eventId) || experience == null)
            return false;
        try
        {
            EnsureRegistryFile();
            var path = LessRealBoothPaths.EventsRegistryJson;
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<EventsRegistryFile>(json, JsonOpts) ?? new EventsRegistryFile();
            var ev = file.Events.FirstOrDefault(x =>
                string.Equals(x.Id, eventId, StringComparison.OrdinalIgnoreCase));
            if (ev == null)
                return false;
            ev.Experience = CloneExperience(experience);
            File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOpts));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BoothEventExperienceSettings CloneExperience(BoothEventExperienceSettings? s)
    {
        if (s == null) return new BoothEventExperienceSettings();
        return new BoothEventExperienceSettings
        {
            StartScreenRelativePath = s.StartScreenRelativePath,
            PreRollVideoRelativePath = s.PreRollVideoRelativePath,
            PlayPreRollBeforeEachPhoto = s.PlayPreRollBeforeEachPhoto
        };
    }

    private sealed class EventsRegistryFile
    {
        public List<EventRecord> Events { get; set; } = [];
    }

    private sealed class EventRecord
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public DateTime CreatedUtc { get; set; }
        public bool IsArchived { get; set; }
        /// <summary>When null, every catalog + built-in layout is available.</summary>
        public List<string>? EnabledLayoutIds { get; set; }
        public BoothEventExperienceSettings? Experience { get; set; }
    }
}
