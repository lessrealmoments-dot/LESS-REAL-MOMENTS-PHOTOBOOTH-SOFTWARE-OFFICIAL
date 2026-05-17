using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace BoothDesktop.Models;

/// <summary>Paths are relative to the event folder under Documents/LessRealBooth/events/… (see <c>media/</c>).</summary>
public sealed class BoothEventExperienceSettings
{
    public string? StartScreenRelativePath { get; set; }
    public string? PreRollVideoRelativePath { get; set; }
    public bool PlayPreRollBeforeEachPhoto { get; set; }
}

/// <summary>Windows print queue + driver preferences for one virtual booth printer slot.</summary>
public sealed class PrinterSlotSettings
{
    /// <summary>Installed queue name; null/empty uses Windows default printer when printing.</summary>
    public string? PrinterName { get; set; }
    public int Copies { get; set; } = 1;
    /// <summary>Operator label, e.g. "4×6 4R" or "2×6 strip (2-up)".</summary>
    public string? ProfileLabel { get; set; }
    /// <summary>Base64 DEVMODE from Windows driver Preferences (Configure printer).</summary>
    public string? DriverPreferencesBase64 { get; set; }

    public bool HasDriverPreferences => !string.IsNullOrWhiteSpace(DriverPreferencesBase64);

    public byte[]? GetDriverPreferencesBytes()
    {
        if (string.IsNullOrWhiteSpace(DriverPreferencesBase64)) return null;
        try
        {
            return Convert.FromBase64String(DriverPreferencesBase64);
        }
        catch
        {
            return null;
        }
    }

    public void SetDriverPreferencesBytes(byte[]? devMode)
    {
        DriverPreferencesBase64 = devMode is { Length: > 0 }
            ? Convert.ToBase64String(devMode)
            : null;
    }

    public void ClearDriverPreferences()
    {
        DriverPreferencesBase64 = null;
    }

    /// <summary>Print size tuning (100 = default fit). dslrBooth-style alignment scale.</summary>
    public int AlignmentScalePercent { get; set; } = 100;

    /// <summary>Horizontal nudge in 1/100 inch; positive moves print right.</summary>
    public int AlignmentOffsetXHundredths { get; set; }

    /// <summary>Vertical nudge in 1/100 inch; positive moves print down.</summary>
    public int AlignmentOffsetYHundredths { get; set; }

    /// <summary>Printer 2 only: use Printer 1 scale and position offsets.</summary>
    public bool FollowPrinter1Alignment { get; set; }
}

/// <summary>App-wide operator settings (storage root, dual printers).</summary>
public sealed class GlobalAppSettings
{
    /// <summary>
    /// Absolute path where LessRealBooth data lives (events/, catalog/, logs/).
    /// When null/empty, app falls back to Documents\LessRealBooth.
    /// </summary>
    public string? StorageRootPath { get; set; }

    public PrinterSlotSettings Printer1 { get; set; } = new();
    public PrinterSlotSettings Printer2 { get; set; } = new();

    public PrintBehaviorSettings PrintBehavior { get; set; } = new();

    /// <summary>Legacy single-printer field; migrated into <see cref="Printer1"/> on load.</summary>
    public string? PreferredPrinterName { get; set; }
}

/// <summary>Global print behavior (dslrBooth-style print setup options).</summary>
public sealed class PrintBehaviorSettings
{
    public bool PrintAutomatically { get; set; }
    public bool ShowPrintButton { get; set; } = true;
    public bool PrintToBothPrinters { get; set; }
    public bool LimitPrints { get; set; } = true;
    public int MaxPrintsPerEvent { get; set; } = 100;
    public int MaxPrintsPerSession { get; set; } = 20;
    /// <summary>Max copies per guest print action (future copy picker).</summary>
    public int PrintDialogMaxCopies { get; set; } = 10;
    /// <summary>None, Low, Medium, High — compositor sharpening (Phase 2 stores; apply later).</summary>
    public string PrintSharpening { get; set; } = "Medium";

    /// <summary>
    /// When true, route final composite rendering through the libvips/NetVips compositor (Lanczos3,
    /// streaming, low RAM). Falls back to the WPF compositor automatically if libvips fails to load
    /// or a render throws. Default false during Phase 2 rollout; flip per-machine via global_settings.json
    /// or via the future Settings UI toggle. Marked for default = true once A/B verification passes.
    /// </summary>
    public bool UseVipsCompositor { get; set; }
}

public sealed class BoothEventSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DateTime CreatedUtc { get; init; }
    public bool IsArchived { get; init; }
    /// <summary>Start screen image, pre-roll video, and related guest-flow options.</summary>
    public BoothEventExperienceSettings Experience { get; init; } = new();
    /// <summary>
    /// When null, all imported catalog layouts and built-ins appear in the booth picker.
    /// When set, only these layout ids (catalog guids or lay_af1, lay_h2, lay_h3, …).
    /// </summary>
    public List<string>? EnabledLayoutIds { get; init; }

    public bool IsLayoutVisible(string layoutId)
    {
        if (string.IsNullOrWhiteSpace(layoutId)) return false;
        if (EnabledLayoutIds == null || EnabledLayoutIds.Count == 0)
            return true;
        return EnabledLayoutIds.Contains(layoutId, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// A selectable layout (from imported template or mock).
/// </summary>
public sealed class BoothLayoutOption : INotifyPropertyChanged
{
    private string? _resolvedPreviewPath;

    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Distinct capture count (duplicate slots share one photo).</summary>
    public int ShotCount { get; init; }
    /// <summary>Mini thumbnail style: af1, h2, h3 (matches template packs).</summary>
    public string PreviewKey { get; init; } = "af1";
    /// <summary>Resolved path to preview.png (Documents or app Assets).</summary>
    public string? ResolvedPreviewPath
    {
        get => _resolvedPreviewPath;
        set
        {
            if (_resolvedPreviewPath == value) return;
            _resolvedPreviewPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPreviewFile));
        }
    }

    public bool HasPreviewFile =>
        !string.IsNullOrEmpty(_resolvedPreviewPath) && File.Exists(_resolvedPreviewPath);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Per-event caps (Phase 1 uses mock defaults).
/// </summary>
public sealed class SessionLimitConfig
{
    public int MaxPhotosPerSession { get; init; } = 8;
    public int MaxPrintsPerEvent { get; init; } = 200;
    public int MaxPrintsPerSession { get; init; } = 2;
}

public sealed class ActiveSessionState
{
    public required string EventId { get; init; }
    public required BoothLayoutOption Layout { get; init; }
    public required SessionLimitConfig Limits { get; init; }
    public int PhotosTaken { get; set; }
    public int PrintsThisSession { get; set; }
    public int PrintsTotalEvent { get; set; }

    public bool CanTakePhoto => PhotosTaken < Math.Min(Layout.ShotCount, Limits.MaxPhotosPerSession);
    public bool CanPrintSession => PrintsThisSession < Limits.MaxPrintsPerSession;
    public bool CanPrintEvent => PrintsTotalEvent < Limits.MaxPrintsPerEvent;
    public bool CanPrint => CanPrintSession && CanPrintEvent;
}
