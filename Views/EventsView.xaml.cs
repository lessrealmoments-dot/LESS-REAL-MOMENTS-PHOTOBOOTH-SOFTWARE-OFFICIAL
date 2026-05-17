using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BoothDesktop.Models;
using BoothDesktop.Services;
using Microsoft.Win32;

namespace BoothDesktop.Views;

public partial class EventsView : UserControl
{
    public static readonly DependencyProperty IsCameraReadyProperty =
        DependencyProperty.Register(
            nameof(IsCameraReady),
            typeof(bool),
            typeof(EventsView),
            new PropertyMetadata(false));

    public bool IsCameraReady
    {
        get => (bool)GetValue(IsCameraReadyProperty);
        private set => SetValue(IsCameraReadyProperty, value);
    }

    private readonly INavigationHost _nav;
    private readonly DispatcherTimer _cameraTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _cameraProbeBusy;

    public EventsView(INavigationHost nav)
    {
        _nav = nav;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _cameraTimer.Tick += async (_, _) => await RefreshCameraStatusAsync(includeReconnectAttempt: false);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ReloadEventsList();
        await RefreshCameraStatusAsync(includeReconnectAttempt: false);
        _cameraTimer.Start();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _cameraTimer.Stop();
    }

    private void ReloadEventsList()
    {
        EventRegistryService.EnsureRegistryFile();
        var items = new ObservableCollection<BoothEventSummary>(EventRegistryService.LoadEvents());
        EventsList.ItemsSource = items;
    }

    private void OnOpenBoothClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BoothEventSummary ev }) return;

        if (!IsCameraReady)
        {
            MessageBox.Show(
                "Wait for the camera to connect.",
                "Camera not ready",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _nav.EnterPhotobooth(ev);
    }

    private void OnNewEventClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Phase 2: new event wizard (name, start screen, layouts, limits).",
            "BoothDesktop", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnImportLayoutClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import dslrBooth layout pack",
            Filter = "ZIP layout (*.zip)|*.zip|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var (ok, msg) = LayoutCatalogService.TryImportZip(dlg.FileName);
        MessageBox.Show(msg, ok ? "Layout imported" : "Import failed",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnGlobalSettingsClick(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this) ?? Application.Current.MainWindow as Window;
        if (owner == null)
        {
            MessageBox.Show("Could not open dialog (no parent window).", "BoothDesktop",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new GlobalSettingsWindow(owner);
        if (dlg.ShowDialog() == true)
            ReloadEventsList();
    }

    private void OnEventLayoutsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BoothEventSummary ev }) return;
        var owner = Window.GetWindow(this) ?? Application.Current.MainWindow as Window;
        if (owner == null)
        {
            MessageBox.Show("Could not open dialog (no parent window).", "BoothDesktop",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pruned = LayoutCatalogService.PruneMissingFromCatalog();
        if (pruned > 0)
            ReloadEventsList();

        var dlg = new EventLayoutSetupWindow(ev, owner);
        if (dlg.ShowDialog() == true)
            ReloadEventsList();
    }

    private async void OnRefreshCameraClick(object sender, RoutedEventArgs e)
    {
        await RefreshCameraStatusAsync(includeReconnectAttempt: false);
    }

    private async void OnReconnectCameraClick(object sender, RoutedEventArgs e)
    {
        await RefreshCameraStatusAsync(includeReconnectAttempt: true);
    }

    private async Task RefreshCameraStatusAsync(bool includeReconnectAttempt)
    {
        if (Interlocked.CompareExchange(ref _cameraProbeBusy, 1, 0) != 0)
            return;

        try
        {
            CameraStatusTitle.Text = includeReconnectAttempt ? "Camera: reconnecting..." : "Camera: checking...";
            CameraStatusDetail.Text = "probing sony_bridge health";
            CameraStatusDot.Fill = new SolidColorBrush(Color.FromRgb(192, 144, 42));
            SetCameraReady(false);

            if (includeReconnectAttempt)
                await SonyBridgeLauncher.EnsureStartedAsync();

            var snap = await BridgeDiagnosticsService.ProbeAsync();
            var ready = snap.IsReadyForCapture;

            if (ready)
            {
                CameraStatusTitle.Text = "Camera: detected";
                CameraStatusDetail.Text = "ready for capture";
                CameraStatusDot.Fill = new SolidColorBrush(Color.FromRgb(88, 186, 104));
            }
            else if (snap.HealthLine.Contains("connected=false", StringComparison.OrdinalIgnoreCase))
            {
                CameraStatusTitle.Text = "Camera: not detected";
                CameraStatusDetail.Text = "connect camera / check USB mode";
                CameraStatusDot.Fill = new SolidColorBrush(Color.FromRgb(217, 122, 61));
            }
            else
            {
                CameraStatusTitle.Text = "Camera: not recognized";
                var hint = snap.HealthLine.Length > 140 ? snap.HealthLine[..140] + "…" : snap.HealthLine;
                CameraStatusDetail.Text = string.IsNullOrWhiteSpace(hint)
                    ? "bridge not responding or /health JSON unexpected"
                    : hint;
                CameraStatusDot.Fill = new SolidColorBrush(Color.FromRgb(210, 76, 76));
            }

            SetCameraReady(ready);
        }
        catch (Exception ex)
        {
            CameraStatusTitle.Text = "Camera: not recognized";
            CameraStatusDetail.Text = ex.Message;
            CameraStatusDot.Fill = new SolidColorBrush(Color.FromRgb(210, 76, 76));
            SetCameraReady(false);
        }
        finally
        {
            Interlocked.Exchange(ref _cameraProbeBusy, 0);
        }
    }

    private void SetCameraReady(bool ready)
    {
        IsCameraReady = ready;
        CameraNotReadyWarning.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnEventSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BoothEventSummary ev }) return;
        var owner = Window.GetWindow(this) ?? Application.Current.MainWindow as Window;
        if (owner == null)
        {
            MessageBox.Show("Could not open dialog (no parent window).", "BoothDesktop",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new EventSettingsWindow(ev, owner);
        if (dlg.ShowDialog() == true)
            ReloadEventsList();
    }
}
