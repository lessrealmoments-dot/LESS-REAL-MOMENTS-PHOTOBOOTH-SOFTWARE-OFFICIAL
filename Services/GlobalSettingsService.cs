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
                return Normalize(JsonSerializer.Deserialize<GlobalAppSettings>(json, JsonOpts) ?? new GlobalAppSettings());
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
            var clean = Normalize(settings);
            clean = new GlobalAppSettings
            {
                StorageRootPath = string.IsNullOrWhiteSpace(clean.StorageRootPath)
                    ? null
                    : Path.GetFullPath(clean.StorageRootPath.Trim()),
                Printer1 = clean.Printer1,
                Printer2 = clean.Printer2,
                PrintBehavior = clean.PrintBehavior
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

    private static GlobalAppSettings Normalize(GlobalAppSettings settings)
    {
        settings.Printer1 ??= new PrinterSlotSettings();
        settings.Printer2 ??= new PrinterSlotSettings();
        settings.PrintBehavior ??= new PrintBehaviorSettings();
        settings.PrintBehavior.PrintSharpening = NormalizeSharpening(settings.PrintBehavior.PrintSharpening);
        settings.PrintBehavior.MaxPrintsPerEvent = Math.Clamp(settings.PrintBehavior.MaxPrintsPerEvent, 1, 9999);
        settings.PrintBehavior.MaxPrintsPerSession = Math.Clamp(settings.PrintBehavior.MaxPrintsPerSession, 1, 99);
        settings.PrintBehavior.PrintDialogMaxCopies = Math.Clamp(settings.PrintBehavior.PrintDialogMaxCopies, 1, 99);
        settings.Printer1.Copies = Math.Clamp(settings.Printer1.Copies, 1, 99);
        settings.Printer2.Copies = Math.Clamp(settings.Printer2.Copies, 1, 99);

        settings.Printer1.PrinterName = string.IsNullOrWhiteSpace(settings.Printer1.PrinterName)
            ? null
            : settings.Printer1.PrinterName.Trim();
        settings.Printer2.PrinterName = string.IsNullOrWhiteSpace(settings.Printer2.PrinterName)
            ? null
            : settings.Printer2.PrinterName.Trim();

        settings.Printer1.ProfileLabel = string.IsNullOrWhiteSpace(settings.Printer1.ProfileLabel)
            ? null
            : settings.Printer1.ProfileLabel.Trim();
        settings.Printer2.ProfileLabel = string.IsNullOrWhiteSpace(settings.Printer2.ProfileLabel)
            ? null
            : settings.Printer2.ProfileLabel.Trim();

        PrinterAlignmentResolver.NormalizeSlot(settings.Printer1);
        PrinterAlignmentResolver.NormalizeSlot(settings.Printer2);

        if (string.IsNullOrWhiteSpace(settings.Printer1.PrinterName)
            && !string.IsNullOrWhiteSpace(settings.PreferredPrinterName))
            settings.Printer1.PrinterName = settings.PreferredPrinterName.Trim();

        settings.PreferredPrinterName = null;
        return settings;
    }

    private static string NormalizeSharpening(string? value) =>
        value switch
        {
            "None" or "Low" or "Medium" or "High" => value,
            _ => "Medium"
        };
}
