using System.Collections.ObjectModel;
using System.ComponentModel;
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
        var rows = new ObservableCollection<LayoutCheckRow>();
        var allowed = _event.EnabledLayoutIds;
        var useAllDefault = allowed == null || allowed.Count == 0;

        foreach (var lo in BuiltInLayouts.All())
        {
            rows.Add(new LayoutCheckRow
            {
                LayoutId = lo.Id,
                Label = $"{lo.DisplayName}  (built-in · {lo.Id})",
                IsChecked = useAllDefault || allowed!.Contains(lo.Id, StringComparer.OrdinalIgnoreCase)
            });
        }

        foreach (var lo in LayoutCatalogService.ToBoothOptions(LayoutCatalogService.LoadCatalogEntries()))
        {
            var idNote = lo.Id.Length > 10 ? $"{lo.Id[..8]}…" : lo.Id;
            rows.Add(new LayoutCheckRow
            {
                LayoutId = lo.Id,
                Label = $"{lo.DisplayName}  (imported · {idNote})",
                IsChecked = useAllDefault || allowed!.Contains(lo.Id, StringComparer.OrdinalIgnoreCase)
            });
        }

        LayoutChecksList.ItemsSource = rows;
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
        public required string Label { get; init; }

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
