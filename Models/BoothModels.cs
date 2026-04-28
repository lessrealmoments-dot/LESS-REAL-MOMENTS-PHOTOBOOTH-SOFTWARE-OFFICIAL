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

/// <summary>App-wide operator settings (storage root, printer preference).</summary>
public sealed class GlobalAppSettings
{
    /// <summary>
    /// Absolute path where LessRealBooth data lives (events/, catalog/, logs/).
    /// When null/empty, app falls back to Documents\LessRealBooth.
    /// </summary>
    public string? StorageRootPath { get; set; }
    /// <summary>Optional preferred printer display name.</summary>
    public string? PreferredPrinterName { get; set; }
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
