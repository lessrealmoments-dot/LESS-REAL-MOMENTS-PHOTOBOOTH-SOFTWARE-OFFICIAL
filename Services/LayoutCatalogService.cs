using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using BoothDesktop.Models;

namespace BoothDesktop.Services;

public static class LayoutCatalogService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void EnsureCatalogDirectories()
    {
        Directory.CreateDirectory(LessRealBoothPaths.CatalogRoot);
        Directory.CreateDirectory(LessRealBoothPaths.CatalogLayoutsStore);
    }

    public static List<LayoutCatalogEntry> LoadCatalogEntries()
    {
        try
        {
            var path = LessRealBoothPaths.CatalogLayoutsJson;
            if (!File.Exists(path)) return [];
            var json = File.ReadAllText(path);
            var root = JsonSerializer.Deserialize<CatalogFile>(json, JsonOpts);
            return root?.Layouts ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void SaveCatalogEntries(List<LayoutCatalogEntry> layouts)
    {
        EnsureCatalogDirectories();
        var file = new CatalogFile { Layouts = layouts.OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase).ToList() };
        File.WriteAllText(LessRealBoothPaths.CatalogLayoutsJson, JsonSerializer.Serialize(file, JsonOpts));
    }

    /// <summary>Maps catalog rows to booth layout options (preview path set when preview.png exists).</summary>
    public static IEnumerable<BoothLayoutOption> ToBoothOptions(IEnumerable<LayoutCatalogEntry> entries)
    {
        foreach (var e in entries)
        {
            var rootDir = Path.Combine(LessRealBoothPaths.CatalogLayoutsStore, e.Folder);
            var preview = LayoutPreviewResolver.FindPreviewPngUnderDirectory(rootDir);

            var opt = new BoothLayoutOption
            {
                Id = e.Id,
                DisplayName = e.DisplayName,
                ShotCount = e.ShotCount,
                PreviewKey = GuessPreviewKey(e.ShotCount)
            };
            if (!string.IsNullOrEmpty(preview))
                opt.ResolvedPreviewPath = preview;
            yield return opt;
        }
    }

    private static string GuessPreviewKey(int shots)
    {
        if (shots <= 2) return "h3";
        if (shots == 4) return "h2";
        return "af1";
    }

    /// <summary>Import a dslrBooth-style ZIP (template.xml + preview.png + assets).</summary>
    public static (bool Ok, string Message) TryImportZip(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return (false, "File not found.");

        try
        {
            EnsureCatalogDirectories();
            var id = Guid.NewGuid().ToString("N");
            var dest = Path.Combine(LessRealBoothPaths.CatalogLayoutsStore, id);
            if (Directory.Exists(dest))
                Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);
            ZipFile.ExtractToDirectory(zipPath, dest);

            var templatePath = Directory.GetFiles(dest, "template.xml", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (templatePath == null)
                return (false, "No template.xml found in ZIP.");

            if (!TryParseTemplate(templatePath, out var displayName, out var shotCount, out var err))
                return (false, err);

            var list = LoadCatalogEntries();
            list.Add(new LayoutCatalogEntry
            {
                Id = id,
                DisplayName = displayName,
                Folder = id,
                ShotCount = shotCount,
                ImportedUtc = DateTime.UtcNow
            });
            SaveCatalogEntries(list);

            return (true,
                $"Imported “{displayName}” ({shotCount} photos).\r\nStored under Documents\\LessRealBooth\\catalog\\layouts\\{id}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static bool TryParseTemplate(string templatePath, out string displayName, out int shotCount, out string error)
    {
        displayName = Path.GetFileNameWithoutExtension(templatePath);
        shotCount = 0;
        error = "";

        try
        {
            var doc = new XmlDocument();
            doc.Load(templatePath);
            var root = doc.DocumentElement;
            if (root != null && root.HasAttribute("Name"))
                displayName = root.GetAttribute("Name");

            var nums = new HashSet<int>();
            var photos = doc.GetElementsByTagName("Photo");
            foreach (XmlNode node in photos)
            {
                if (node is not XmlElement p) continue;
                if (!p.HasAttribute("PhotoNumber")) continue;
                if (int.TryParse(p.GetAttribute("PhotoNumber"), out var n))
                    nums.Add(n);
            }

            if (nums.Count == 0)
            {
                error = "template.xml has no <Photo PhotoNumber=\"…\"> elements.";
                return false;
            }

            shotCount = nums.Count;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private sealed class CatalogFile
    {
        public List<LayoutCatalogEntry> Layouts { get; set; } = [];
    }
}

public sealed class LayoutCatalogEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Folder { get; init; }
    public int ShotCount { get; init; }
    public DateTime ImportedUtc { get; init; }
}
