using System.IO;
using System.IO.Compression;

namespace BoothDesktop.Services;

public static class LayoutPreviewResolver
{
    /// <summary>
    /// Resolve preview.png for a layout id.
    /// Order: Documents layout_previews, loose file under Assets/Layouts/{id}/,
    /// then preview.png inside any *.zip in that folder (dslrBooth export).
    /// </summary>
    public static string? Resolve(string layoutId)
    {
        if (string.IsNullOrWhiteSpace(layoutId)) return null;

        var docPath = LessRealBoothPaths.LayoutPreviewFile(layoutId);
        if (File.Exists(docPath)) return docPath;

        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var layoutDir = Path.Combine(baseDir, "Assets", "Layouts", layoutId);
        var bundled = Path.Combine(layoutDir, "preview.png");
        if (File.Exists(bundled)) return bundled;

        return TryMaterializePreviewFromBundledZip(layoutId, layoutDir);
    }

    /// <summary>First preview.png under a directory tree (imported catalog extract).</summary>
    public static string? FindPreviewPngUnderDirectory(string rootDir)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return null;

        try
        {
            foreach (var f in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(f), "preview.png", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
        }
        catch
        {
            /* ignore */
        }

        return null;
    }

    private static string? TryMaterializePreviewFromBundledZip(string layoutId, string layoutDir)
    {
        if (!Directory.Exists(layoutDir)) return null;

        string[] zips;
        try
        {
            zips = Directory.GetFiles(layoutDir, "*.zip", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return null;
        }

        Array.Sort(zips, StringComparer.OrdinalIgnoreCase);
        foreach (var zipPath in zips)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                ZipArchiveEntry? best = null;
                var bestDepth = int.MaxValue;
                foreach (var e in archive.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    if (e.FullName.Contains("..", StringComparison.Ordinal)) continue;
                    if (!string.Equals(e.Name, "preview.png", StringComparison.OrdinalIgnoreCase)) continue;
                    var depth = e.FullName.Count(c => c == '/' || c == '\\');
                    if (depth < bestDepth)
                    {
                        bestDepth = depth;
                        best = e;
                    }
                }

                if (best == null) continue;

                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BoothDesktop", "layout_preview_cache", layoutId);
                Directory.CreateDirectory(cacheDir);
                var dest = Path.Combine(cacheDir, "preview.png");
                var marker = Path.Combine(cacheDir, ".source_zip");
                var zipUtc = File.GetLastWriteTimeUtc(zipPath).Ticks;
                var zipName = Path.GetFileName(zipPath);
                var needWrite = true;
                if (File.Exists(dest) && File.Exists(marker))
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
                    using (var input = best.Open())
                    using (var output = File.Create(dest))
                        input.CopyTo(output);
                    File.WriteAllText(marker, zipUtc + Environment.NewLine + zipName);
                }

                return dest;
            }
            catch
            {
                /* try next zip */
            }
        }

        return null;
    }
}
