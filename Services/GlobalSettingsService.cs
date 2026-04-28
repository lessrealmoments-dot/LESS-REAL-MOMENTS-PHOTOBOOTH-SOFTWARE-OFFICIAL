using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoothDesktop.Models;

namespace BoothDesktop.Services;

/// <summary>
/// Persists app-wide settings in LocalAppData so data-root changes do not hide the settings file.
/// </summary>
public static class GlobalSettingsService
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LessRealBooth");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "global_settings.json");

    public static GlobalAppSettings Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new GlobalAppSettings();
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<GlobalAppSettings>(json, JsonOpts) ?? new GlobalAppSettings();
            }
            catch
            {
                return new GlobalAppSettings();
            }
        }
    }

    public static bool TrySave(GlobalAppSettings settings, out string error)
    {
        error = "";
        if (settings == null)
        {
            error = "Settings are empty.";
            return false;
        }

        try
        {
            var clean = new GlobalAppSettings
            {
                StorageRootPath = string.IsNullOrWhiteSpace(settings.StorageRootPath)
                    ? null
                    : Path.GetFullPath(settings.StorageRootPath.Trim()),
                PreferredPrinterName = string.IsNullOrWhiteSpace(settings.PreferredPrinterName)
                    ? null
                    : settings.PreferredPrinterName.Trim()
            };

            if (!string.IsNullOrWhiteSpace(clean.StorageRootPath))
                Directory.CreateDirectory(clean.StorageRootPath);
            Directory.CreateDirectory(SettingsDir);

            lock (Gate)
            {
                var json = JsonSerializer.Serialize(clean, JsonOpts);
                File.WriteAllText(SettingsPath, json);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
