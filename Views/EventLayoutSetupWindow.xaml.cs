using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using BoothDesktop.Models;
using BoothDesktop.Services;

namespace BoothDesktop.Views;

public partial class EventLayoutSetupWindow : Window
{
    private readonly BoothEventSummary _event;

    public EventLayoutSetupWindow(BoothEventSummary boothEvent, Window owner)
    {
        _event = boothEvent;
        Owner = owner;
        InitializeComponent();
        Title = $"Layouts — {boothEvent.Name}";
        Loaded += (_, _) => PopulateRows();
    }

    private void PopulateRows()
    {
        var pruned = LayoutCatalogService.PruneMissingFromCatalog();
        if (pruned > 0)
            RuntimeLog.Info("Layouts", $"removed {pruned} missing import(s) from catalog");

        var availableIds = LayoutCatalogService.CollectAvailableLayoutIds();
        var allowed = SanitizeEnabledLayoutIds(_event.EnabledLayoutIds, availableIds);
        PersistSanitizedAllowlistIfNeeded(allowed);

        var rows = new ObservableCollection<LayoutCheckRow>();
        var useAllDefault = allowed == null || allowed.Count == 0;

        foreach (var lo in BuiltInLayouts.All())
        {
            rows.Add(new LayoutCheckRow
            {
                LayoutId = lo.Id,
                DisplayName = lo.DisplayName,
                PreviewPath = lo.ResolvedPreviewPath,
                IsChecked = useAllDefault || allowed!.Contains(lo.Id, StringComparer.OrdinalIgnoreCase)
            });
        }

        foreach (var lo in LayoutCatalogService.ToBoothOptions(LayoutCatalogService.LoadAvailableCatalogEntries()))
        {
            rows.Add(new LayoutCheckRow
            {
                LayoutId = lo.Id,
                DisplayName = lo.DisplayName,
                PreviewPath = lo.ResolvedPreviewPath,
                IsChecked = useAllDefault || allowed!.Contains(lo.Id, StringComparer.OrdinalIgnoreCase)
            });
        }

        LayoutChecksList.ItemsSource = rows;

        if (rows.Count == 0)
        {
            MessageBox.Show(this,
                "No layouts are available. Import a layout ZIP from the Events screen, or add preview packs under Assets\\Layouts.",
                "Layouts", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void PersistSanitizedAllowlistIfNeeded(List<string>? cleaned)
    {
        var orig = _event.EnabledLayoutIds;
        if (orig == null || orig.Count == 0)
            return;

        var same = cleaned != null && cleaned.Count == orig.Count
            && cleaned.All(id => orig.Contains(id, StringComparer.OrdinalIgnoreCase));
        if (same)
            return;

        EventRegistryService.TrySetEnabledLayouts(_event.Id, cleaned);
    }

    /// <summary>Drop ids that point at deleted imports.</summary>
    private static List<string>? SanitizeEnabledLayoutIds(IReadOnlyList<string>? enabled, HashSet<string> availableIds)
    {
        if (enabled == null || enabled.Count == 0)
            return null;

        var cleaned = enabled.Where(id => availableIds.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cleaned.Count == enabled.Count)
            return cleaned;

        RuntimeLog.Info("Layouts",
            $"event allowlist: removed {enabled.Count - cleaned.Count} missing layout id(s)");
        return cleaned.Count == 0 ? null : cleaned;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (LayoutChecksList.ItemsSource is not ObservableCollection<LayoutCheckRow> rows)
        {
            DialogResult = false;
            return;
        }

        var selected = rows.Where(r => r.IsChecked).Select(r => r.LayoutId).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select at least one layout.", "Layouts", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!EventRegistryService.TrySaveEnabledLayouts(_event.Id, selected))
        {
            MessageBox.Show(this, "Could not save. Check that this event still exists in events_registry.json.",
                "Layouts", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    public sealed class LayoutCheckRow : INotifyPropertyChanged
    {
        private bool _isChecked;

        public required string LayoutId { get; init; }
        public required string DisplayName { get; init; }
        public string? PreviewPath { get; init; }

        public bool HasPreview =>
            !string.IsNullOrWhiteSpace(PreviewPath) && File.Exists(PreviewPath);

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
