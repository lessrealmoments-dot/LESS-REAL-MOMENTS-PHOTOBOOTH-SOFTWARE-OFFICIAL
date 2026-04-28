using System.IO;
using System.IO.Compression;
using System.Linq;

namespace BoothDesktop.Services;

/// <summary>
/// Resolves a layout id to a folder containing template.xml + assets (catalog extract, loose Assets, or full ZIP extract cache).
/// </summary>
public static class LayoutPackService
{
    private static string PackCacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BoothDesktop", "layout_pack_cache");

    /// <summary>Returns directory that contains template.xml (possibly nested); null if not found.</summary>
    public static string? TryGetPackRoot(string layoutId)
    {
        if (string.IsNullOrWhiteSpace(layoutId)) return null;

        var catalogDir = Path.Combine(LessRealBoothPaths.CatalogLayoutsStore, layoutId);
        if (Directory.Exists(catalogDir))
        {
            var t = FindTemplateXmlPath(catalogDir);
            if (t != null) return Path.GetDirectoryName(t)!;
        }

        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var assetsLayoutDir = Path.Combine(baseDir, "Assets", "Layouts", layoutId);
        if (Directory.Exists(assetsLayoutDir))
        {
            var loose = FindTemplateXmlPath(assetsLayoutDir);
            if (loose != null) return Path.GetDirectoryName(loose)!;

            string[] zips;
            try
            {
                zips = Directory.GetFiles(assetsLayoutDir, "*.zip", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return null;
            }

            Array.Sort(zips, StringComparer.OrdinalIgnoreCase);
            foreach (var zip in zips)
            {
                var extracted = EnsureZipExtracted(layoutId, zip);
                if (extracted == null) continue;
                var t = FindTemplateXmlPath(extracted);
                if (t != null) return Path.GetDirectoryName(t)!;
            }
        }

        return null;
    }

    public static string? FindTemplateXmlPath(string rootDir)
    {
        if (!Directory.Exists(rootDir)) return null;
        try
        {
            return Directory.GetFiles(rootDir, "template.xml", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? EnsureZipExtracted(string layoutId, string zipPath)
    {
        try
        {
            var cacheDir = Path.Combine(PackCacheRoot, layoutId, "extracted");
            var marker = Path.Combine(PackCacheRoot, layoutId, ".pack_zip_source");
            var zipUtc = File.GetLastWriteTimeUtc(zipPath).Ticks;
            var zipName = Path.GetFileName(zipPath);
            var needWrite = true;
            if (Directory.Exists(cacheDir) && File.Exists(marker))
            {
                var lines = File.ReadAllLines(marker);
                if (lines.Length >= 2 &&
                    long.TryParse(lines[0], out var oldTicks) &&
                    string.Equals(lines[1], zipName, StringComparison.OrdinalIgnoreCase) &&
                    oldTicks == zipUtc)
                    needWrite = false;
            }

            if (needWrite)
            {
                if (Directory.Exists(cacheDir))
                    Directory.Delete(cacheDir, true);
                Directory.CreateDirectory(cacheDir);
                ZipFile.ExtractToDirectory(zipPath, cacheDir);
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, zipUtc + Environment.NewLine + zipName);
            }

            return cacheDir;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve template.xml asset path to a file under the layout pack (catalog extract or ZIP extract).
    /// Uses exact relative path first, then same file name anywhere under pack (dslrBooth paths often differ from extract layout).
    /// </summary>
    public static string? ResolvePackAssetFile(string packRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(packRoot) || string.IsNullOrWhiteSpace(relativePath)) return null;
        if (!Directory.Exists(packRoot)) return null;

        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (rel.Contains(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || rel.Equals("..", StringComparison.Ordinal)
            || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;

        var rootFull = Path.GetFullPath(packRoot);
        var direct = Path.GetFullPath(Path.Combine(packRoot, rel));
        if (!direct.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return null;
        if (File.Exists(direct))
            return direct;

        var name = Path.GetFileName(rel);
        if (string.IsNullOrEmpty(name)) return null;

        try
        {
            return Directory.EnumerateFiles(packRoot, name, SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .FirstOrDefault(p => p.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }
}
