using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using BoothDesktop.Models;
using BoothDesktop.Services;
using Microsoft.Win32;

namespace BoothDesktop.Views;

public partial class EventSettingsWindow : Window
{
    private readonly BoothEventSummary _event;
    private BoothEventExperienceSettings _draft = new();

    public EventSettingsWindow(BoothEventSummary boothEvent, Window owner)
    {
        _event = boothEvent;
        Owner = owner;
        InitializeComponent();
        Title = $"Event settings — {boothEvent.Name}";
        Loaded += (_, _) => LoadDraft();
    }

    private void LoadDraft()
    {
        _draft = EventRegistryService.GetExperience(_event.Id);
        PlayPreRollCheck.IsChecked = _draft.PlayPreRollBeforeEachPhoto;
        RefreshUi();
    }

    private void RefreshUi()
    {
        StartScreenRelText.Text = string.IsNullOrEmpty(_draft.StartScreenRelativePath)
            ? "—"
            : _draft.StartScreenRelativePath;
        PreRollRelText.Text = string.IsNullOrEmpty(_draft.PreRollVideoRelativePath)
            ? "—"
            : _draft.PreRollVideoRelativePath;

        var startFull = LessRealBoothPaths.TryResolveEventMediaFile(_event.Id, _event.Name, _draft.StartScreenRelativePath);
        if (startFull != null)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(startFull, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                StartScreenPreview.Source = bmp;
                StartScreenPreview.Visibility = Visibility.Visible;
                StartScreenPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch
            {
                StartScreenPreview.Source = null;
                StartScreenPreview.Visibility = Visibility.Collapsed;
                StartScreenPlaceholder.Visibility = Visibility.Visible;
            }
        }
        else
        {
            StartScreenPreview.Source = null;
            StartScreenPreview.Visibility = Visibility.Collapsed;
            StartScreenPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void OnBrowseStartScreenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Start screen image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        if (!TryCopyToMedia(dlg.FileName, "start_screen", out var rel, out var err))
        {
            MessageBox.Show(this, err, "Event settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _draft.StartScreenRelativePath = rel;
        RefreshUi();
    }

    private void OnClearStartScreenClick(object sender, RoutedEventArgs e)
    {
        TryDeleteMediaPattern("start_screen.*");
        _draft.StartScreenRelativePath = null;
        RefreshUi();
    }

    private void OnBrowsePreRollClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pre-roll video",
            Filter = "Video|*.mp4;*.mov;*.m4v;*.webm|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        if (!TryCopyToMedia(dlg.FileName, "pre_roll", out var rel, out var err))
        {
            MessageBox.Show(this, err, "Event settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _draft.PreRollVideoRelativePath = rel;
        RefreshUi();
    }

    private void OnClearPreRollClick(object sender, RoutedEventArgs e)
    {
        TryDeleteMediaPattern("pre_roll.*");
        _draft.PreRollVideoRelativePath = null;
        RefreshUi();
    }

    private bool TryCopyToMedia(string sourcePath, string baseName, out string relativeToEvent, out string error)
    {
        relativeToEvent = "";
        error = "";
        try
        {
            var mediaDir = LessRealBoothPaths.EventMediaDirectory(_event.Id, _event.Name);
            Directory.CreateDirectory(LessRealBoothPaths.EventRootDirectory(_event.Id, _event.Name));
            Directory.CreateDirectory(mediaDir);

            foreach (var existing in Directory.GetFiles(mediaDir, $"{baseName}.*"))
                File.Delete(existing);

            var ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(ext)) ext = ".bin";
            var destName = baseName + ext;
            var destAbs = Path.Combine(mediaDir, destName);
            File.Copy(sourcePath, destAbs, overwrite: true);
            relativeToEvent = Path.Combine("media", destName).Replace('\\', '/');
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void TryDeleteMediaPattern(string glob)
    {
        try
        {
            var mediaDir = LessRealBoothPaths.EventMediaDirectory(_event.Id, _event.Name);
            if (!Directory.Exists(mediaDir)) return;
            foreach (var f in Directory.GetFiles(mediaDir, glob))
                File.Delete(f);
        }
        catch
        {
            /* ignore */
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _draft.PlayPreRollBeforeEachPhoto = PlayPreRollCheck.IsChecked == true;
        if (!EventRegistryService.TrySaveExperience(_event.Id, _draft))
        {
            MessageBox.Show(this, "Could not save. Check events_registry.json.", "Event settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
