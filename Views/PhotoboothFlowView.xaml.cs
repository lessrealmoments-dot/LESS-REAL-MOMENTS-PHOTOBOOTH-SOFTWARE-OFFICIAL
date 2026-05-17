using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BoothDesktop.Models;
using BoothDesktop.Services;

namespace BoothDesktop.Views;

public partial class PhotoboothFlowView : UserControl
{
    private static readonly HttpClient LiveHttp = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly HttpClient BridgeControlHttp = new() { Timeout = TimeSpan.FromSeconds(2) };
    private const string LiveJpegUrl = "http://127.0.0.1:18080/live.jpg";

    private readonly BoothEventSummary _event;
    private readonly INavigationHost _nav;

    private ActiveSessionState? _session;
    private SessionWorkspace? _workspace;
    private CancellationTokenSource? _flowCts;
    private DispatcherTimer? _stepTimer;
    private DispatcherTimer? _livePollTimer;
    private DispatcherTimer? _diagnosticsTimer;
    private int _countdownValue;
    private int _captureBusy;
    /// <summary>Live poll timer does not await; block overlap so a slow/failed request cannot clear a good frame.</summary>
    private int _livePollBusy;
    private const int MaxCaptureAutoRetries = 5;
    private const double IntroSecondsFirstShot = 2.2;
    private int _captureAutoRetryAttempt;
    /// <summary>Prevents double fire if countdown tick is ever re-entrant.</summary>
    private bool _countdownCaptureScheduled;
    private int _prefocusBusy;
    private bool _prefocusArmed;
    private Action? _preRollContinue;
    private const int FirstShotCountdownSeconds = 8;
    private const int NextShotCountdownSeconds = 5;
    private const int CaptureLeadSeconds = 3;
    private const double ShotReviewSeconds = 3;
    private DispatcherTimer? _shotReviewTimer;
    private int _pendingReviewShotNumber;
    private bool _shotReviewShowing;
    private ParsedTemplate? _parsedLayoutTemplate;
    private EventSessionItem? _viewingSession;
    private DateTime _guestPrintCooldownUntilUtc = DateTime.MinValue;
    private DispatcherTimer? _guestPrintCooldownTimer;

    public PhotoboothFlowView(BoothEventSummary boothEvent, INavigationHost nav)
    {
        _event = boothEvent;
        _nav = nav;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => TearDownCaptureFlow(clearSession: true);
        LivePreviewFrame.SizeChanged += (_, _) => LiveSlotGuide.InvalidateVisual();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        OpeningTitle.Text = _event.Name;
        ApplyOpeningBackground();
        LessRealBoothPaths.EnsureLayoutPreviewHintFile();
        LayoutCatalogService.EnsureCatalogDirectories();
        LoadAllLayouts();
        ShowPanel(PanelOpening);
    }

    private void LoadAllLayouts()
    {
        var layouts = new ObservableCollection<BoothLayoutOption>();
        foreach (var opt in LayoutCatalogService.ToBoothOptions(LayoutCatalogService.LoadAvailableCatalogEntries()))
        {
            if (_event.IsLayoutVisible(opt.Id))
                layouts.Add(opt);
        }

        foreach (var builtIn in BuiltInLayouts.All())
        {
            if (_event.IsLayoutVisible(builtIn.Id))
                layouts.Add(builtIn);
        }

        LayoutList.ItemsSource = layouts;
        LayoutPickEmptyHint.Visibility = layouts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Stops timers, cancels in-flight HTTP to the bridge, clears flow token.</summary>
    private void TearDownCaptureFlow(bool clearSession)
    {
        _guestPrintCooldownTimer?.Stop();
        _guestPrintCooldownTimer = null;
        _guestPrintCooldownUntilUtc = DateTime.MinValue;

        _flowCts?.Cancel();
        _ = SetPrefocusAsync(false, CancellationToken.None);
        _stepTimer?.Stop();
        _stepTimer = null;
        _flowCts?.Dispose();
        _flowCts = null;
        StopLivePolling();
        StopDiagnosticsTimer();
        StopPreRollPlayback();
        StopShotReview();
        Interlocked.Exchange(ref _captureBusy, 0);
        if (clearSession)
        {
            _session = null;
            _workspace = null;
            _parsedLayoutTemplate = null;
        }

        LiveSlotGuide.ClearGuide();
        LiveSlotGuide.Visibility = Visibility.Collapsed;
    }

    private void ShowPanel(UIElement visible)
    {
        StopLivePolling();
        StopDiagnosticsTimer();
        if (visible is not FrameworkElement fe || fe.Parent is not Panel root)
            return;
        foreach (UIElement child in root.Children)
            child.Visibility = child == visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible == PanelCapture)
            StartLivePolling();
    }

    private void StartLivePolling()
    {
        StopLivePolling();
        StopDiagnosticsTimer();
        SetLiveOfflineUi();
        _livePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _livePollTimer.Tick += (_, _) => _ = PollLiveOnceAsync();
        _livePollTimer.Start();
        _ = PollLiveOnceAsync();

        _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _diagnosticsTimer.Tick += (_, _) => _ = UpdateDiagnosticsAsync();
        _diagnosticsTimer.Start();
        _ = UpdateDiagnosticsAsync();
    }

    private void StopLivePolling()
    {
        if (_livePollTimer == null) return;
        _livePollTimer.Stop();
        _livePollTimer = null;
    }

    private void StopDiagnosticsTimer()
    {
        if (_diagnosticsTimer == null) return;
        _diagnosticsTimer.Stop();
        _diagnosticsTimer = null;
        LiveDiagnosticsLine.Text = "";
    }

    private void SetLiveOfflineUi()
    {
        LivePreviewImage.Source = null;
        LivePreviewHint.Visibility = Visibility.Visible;
        LiveBridgeStatus.Text = "Live view: connect sony_bridge (127.0.0.1:18080)";
    }

    private async Task PollLiveOnceAsync()
    {
        if (Interlocked.CompareExchange(ref _livePollBusy, 1, 0) != 0)
            return;

        var token = _flowCts?.Token ?? CancellationToken.None;
        try
        {
            if (token.IsCancellationRequested) return;

            try
            {
                var shm = await Task.Run(() =>
                {
                    var ok = SonyBridgeSharedMemoryReader.TryReadLatestJpeg(out var j, out var fid, out _);
                    return (ok, j, fid);
                }, token).ConfigureAwait(false);
                if (shm.ok && shm.j.Length > 0)
                {
                    var applied = await Dispatcher.InvokeAsync(() =>
                        TryApplyLiveJpeg(shm.j, $"Live · shared memory (frame {shm.fid})", clearUiOnFailure: false));
                    if (applied)
                        return;
                    /* SHM payload can be non-decodable for WPF while HTTP /live.jpg is fine — fall through */
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                /* fall through to HTTP */
            }

            try
            {
                var url = $"{LiveJpegUrl}?cb={DateTime.UtcNow.Ticks}&r={Random.Shared.Next()}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store, max-age=0");
                req.Headers.TryAddWithoutValidation("Pragma", "no-cache");
                using var response = await LiveHttp.SendAsync(req, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() => TryApplyLiveJpeg(bytes, "Live view · HTTP /live.jpg"));
            }
            catch (OperationCanceledException)
            {
                /* flow cancelled */
            }
            catch
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    /* Do not wipe a working preview: overlapping polls used to clear UI on transient errors. */
                    if (LivePreviewImage.Source == null)
                        SetLiveOfflineUi();
                });
            }
        }
        finally
        {
            Interlocked.Exchange(ref _livePollBusy, 0);
        }
    }

    private async Task UpdateDiagnosticsAsync()
    {
        var token = _flowCts?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested) return;
        try
        {
            var snap = await BridgeDiagnosticsService.ProbeAsync(token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => LiveDiagnosticsLine.Text = snap.ToDisplayText());
        }
        catch (OperationCanceledException)
        {
            /* cancelled */
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>Decode JPEG on UI thread (WPF BitmapImage requirement). Returns false if decode failed.</summary>
    private bool TryApplyLiveJpeg(byte[] bytes, string bridgeStatusLine, bool clearUiOnFailure = true)
    {
        BitmapSource? src = null;
        try
        {
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.StreamSource = ms;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
            }

            bmp.Freeze();
            src = bmp;
        }
        catch
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                var dec = new JpegBitmapDecoder(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                var frame = dec.Frames[0];
                frame.Freeze();
                src = frame;
            }
            catch
            {
                if (clearUiOnFailure)
                    SetLiveOfflineUi();
                return false;
            }
        }

        LivePreviewImage.Source = src;
        LivePreviewHint.Visibility = Visibility.Collapsed;
        LiveBridgeStatus.Text = bridgeStatusLine;
        UpdateLiveSlotGuide();
        return true;
    }

    private void OnOpeningTap(object sender, RoutedEventArgs e) => ShowPanel(PanelLayoutPick);

    private void OnViewSessionsClick(object sender, RoutedEventArgs e)
    {
        _ = NavigateToSessionsAsync();
    }

    private void OnSessionsBackClick(object sender, RoutedEventArgs e) => ShowPanel(PanelOpening);

    private void OnSessionDetailBackClick(object sender, RoutedEventArgs e)
    {
        _viewingSession = null;
        ShowPanel(PanelSessions);
    }

    private void OnSessionDetailPrintClick(object sender, RoutedEventArgs e)
    {
        if (_viewingSession == null) return;
        TryGuestPrint(() =>
        {
            if (!SessionPrintService.TryPrintSession(_event.Id, _event.Name, _viewingSession.SessionFolderAbs,
                    out var message))
            {
                MessageBox.Show(message, "Print", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SyncActiveSessionPrintCountsIfSameFolder(_viewingSession.SessionFolderAbs);
            ApplySessionDetailPrintUi();
        });
    }

    private void OnSessionCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SessionCardVm vm }) return;
        _ = NavigateToSessionDetailAsync(vm.Item);
    }

    private async Task NavigateToSessionsAsync()
    {
        ShowPanel(PanelSessions);
        SessionsGrid.ItemsSource = null;

        var sessions = EventSessionCatalogService.LoadSessions(_event);
        var cards = await Task.Run(() =>
            sessions.Select(BuildSessionCardVm).ToList()).ConfigureAwait(true);

        SessionsGrid.ItemsSource = new ObservableCollection<SessionCardVm>(cards);
    }

    private async Task NavigateToSessionDetailAsync(EventSessionItem item)
    {
        _viewingSession = item;
        ShowPanel(PanelSessionDetail);
        SessionDetailOverlay.Source = null;
        SessionDetailGif.Source = null;
        SessionDetailRaws.ItemsSource = null;

        var thumbs = await Task.Run(() => BuildSessionDetailThumbs(item)).ConfigureAwait(true);

        SessionDetailOverlay.Source = LoadThumbBitmap(thumbs.OverlayThumbAbs);
        SessionDetailGif.Source = LoadThumbBitmap(thumbs.GifThumbAbs);
        SessionDetailGif.Visibility = SessionDetailGif.Source != null ? Visibility.Visible : Visibility.Collapsed;
        SessionDetailRaws.ItemsSource = thumbs.RawThumbAbsPaths.Count > 0
            ? new ObservableCollection<string>(thumbs.RawThumbAbsPaths)
            : null;

        ApplySessionDetailPrintUi();
    }

    private void ApplySessionDetailPrintUi()
    {
        if (_viewingSession == null) return;

        var quota = SessionPrintService.Evaluate(_event.Id, _event.Name, _viewingSession.SessionFolderAbs);
        var blocked = quota.LimitsEnabled && !quota.CanPrint;
        SessionDetailPrintButton.Visibility = blocked ? Visibility.Collapsed : Visibility.Visible;
        SessionDetailPrintLimitText.Text = quota.UserMessage;
        SessionDetailPrintLimitText.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
    }

    private static SessionCardVm BuildSessionCardVm(EventSessionItem item)
    {
        var thumb = SessionThumbnailService.EnsureCompositeGridThumb(item.SessionFolderAbs, item.CompositeAbs);
        return new SessionCardVm(item, thumb);
    }

    private static SessionDetailThumbs BuildSessionDetailThumbs(EventSessionItem item)
    {
        var detail = EventSessionCatalogService.LoadDetail(item);
        var folder = item.SessionFolderAbs;
        var overlay = SessionThumbnailService.EnsureCompositeDetailThumb(folder, detail.CompositeAbs);
        var gif = SessionThumbnailService.EnsureGifPreviewThumb(folder, detail.OriginalAbsPaths);
        var raws = detail.OriginalAbsPaths
            .Select(p => SessionThumbnailService.EnsureRawThumb(folder, p))
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToList();
        return new SessionDetailThumbs(overlay, gif, raws);
    }

    private static BitmapImage? LoadThumbBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = SessionThumbnailService.DetailOverlayMax;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private sealed record SessionDetailThumbs(string? OverlayThumbAbs, string? GifThumbAbs, IReadOnlyList<string> RawThumbAbsPaths);

    private sealed class SessionCardVm
    {
        public EventSessionItem Item { get; }
        public string? PreviewThumbAbs { get; }
        public SessionCardVm(EventSessionItem item, string? previewThumbAbs)
        {
            Item = item;
            PreviewThumbAbs = previewThumbAbs;
        }
    }

    private void OnLayoutCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BoothLayoutOption layout }) return;

        TearDownCaptureFlow(clearSession: true);

        _flowCts = new CancellationTokenSource();

        var printBehavior = GlobalSettingsService.Load().PrintBehavior;
        var limits = BuildSessionLimits(printBehavior);
        var eventPrints = EventPrintCounterService.LoadTotalPrints(_event.Id, _event.Name);

        _session = new ActiveSessionState
        {
            EventId = _event.Id,
            Layout = layout,
            Limits = limits,
            PhotosTaken = 0,
            PrintsThisSession = 0,
            PrintsTotalEvent = eventPrints
        };

        _workspace = new SessionWorkspace(_event.Id, _event.Name, layout.Id);

        _parsedLayoutTemplate = null;
        if (LayoutSlotGuideService.TryLoadTemplateForLayout(layout.Id, out var parsed, out var guideErr))
        {
            _parsedLayoutTemplate = parsed;
            RuntimeLog.Info("Flow",
                $"slot guide: template {_parsedLayoutTemplate.CanvasWidth}x{_parsedLayoutTemplate.CanvasHeight} layout={layout.Id}");
        }
        else
            RuntimeLog.Warn("Flow", $"slot guide unavailable layout={layout.Id} err={guideErr}");

        BuildShotDots();
        UpdateLimitsLine();
        ShowPanel(PanelCapture);
        StartCurrentShot();
    }

    private void UpdateLiveSlotGuide()
    {
        if (_parsedLayoutTemplate == null || _session == null || _shotReviewShowing
            || IntroOverlay.Visibility == Visibility.Visible
            || PreRollOverlay.Visibility == Visibility.Visible
            || PanelCapture.Visibility != Visibility.Visible)
        {
            LiveSlotGuide.ClearGuide();
            LiveSlotGuide.Visibility = Visibility.Collapsed;
            return;
        }

        var photoIndex = _session.PhotosTaken + 1;
        if (!LayoutSlotGuideService.TryGetLiveGuideSlotForPhoto(_parsedLayoutTemplate, photoIndex,
                out var slot, out var duplicateSlots))
        {
            LiveSlotGuide.ClearGuide();
            LiveSlotGuide.Visibility = Visibility.Collapsed;
            return;
        }

        if (duplicateSlots > 1)
        {
            RuntimeLog.Info("Flow",
                $"slot guide: photo {photoIndex} has {duplicateSlots} strip positions — showing one framing window for capture");
        }

        LiveSlotGuide.SetCaptureSlotSize(slot.Width, slot.Height);
        LiveSlotGuide.Visibility = Visibility.Visible;
    }

    private void BuildShotDots()
    {
        ShotDotsHost.Children.Clear();
        if (_session == null) return;
        int n = _session.Layout.ShotCount;
        for (int i = 0; i < n; i++)
        {
            var e = new Ellipse
            {
                Width = 14,
                Height = 14,
                Margin = new Thickness(6, 0, 6, 0),
                Stroke = (Brush)FindResource("Brush.Accent"),
                StrokeThickness = 2
            };
            if (i < _session.PhotosTaken)
                e.Fill = (Brush)FindResource("Brush.Accent");
            else
                e.Fill = Brushes.Transparent;
            ShotDotsHost.Children.Add(e);
        }
    }

    private void UpdateLimitsLine()
    {
        if (_session == null) return;
        if (_workspace != null)
            SyncActiveSessionPrintCountsIfSameFolder(_workspace.SessionRoot);
        var t =
            $"Photos {_session.PhotosTaken}/{_session.Layout.ShotCount} · " +
            $"Print actions this session {_session.PrintsThisSession}/{_session.Limits.MaxPrintsPerSession} · " +
            $"Event print actions {_session.PrintsTotalEvent}/{_session.Limits.MaxPrintsPerEvent}";
        LimitsLine.Text = t;
        LimitsLine.ToolTip = t;
    }

    private void StartCurrentShot(bool retryQuick = false)
    {
        if (_session == null || _flowCts == null || _flowCts.Token.IsCancellationRequested) return;
        var token = _flowCts.Token;
        var nextIndex = _session.PhotosTaken + 1;
        ShotStatusLine.Text = $"Photo {nextIndex} of {_session.Layout.ShotCount}";
        var isFirst = _session.PhotosTaken == 0;
        if (!retryQuick)
            _captureAutoRetryAttempt = 0;
        RuntimeLog.Info("Flow", $"start_shot index={nextIndex}/{_session.Layout.ShotCount} retryQuick={retryQuick} photosTaken={_session.PhotosTaken}");
        BuildShotDots();

        CountdownText.Visibility = Visibility.Collapsed;

        if (retryQuick || !isFirst)
        {
            IntroOverlay.Visibility = Visibility.Collapsed;
            BeginPreRollOrCountdown();
            return;
        }

        IntroOverlay.Visibility = Visibility.Visible;
        // Guest-facing: use the event title, not layout DisplayName (often matches zip / template export name).
        IntroLabel.Text = string.IsNullOrWhiteSpace(_event.Name)
            ? "Get ready!"
            : $"Get ready — {_event.Name}";
        _stepTimer?.Stop();
        _stepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(IntroSecondsFirstShot) };
        _stepTimer.Tick += (_, _) =>
        {
            _stepTimer?.Stop();
            if (token.IsCancellationRequested) return;
            IntroOverlay.Visibility = Visibility.Collapsed;
            UpdateLiveSlotGuide();
            BeginPreRollOrCountdown();
        };
        _stepTimer.Start();
    }

    private void ApplyOpeningBackground()
    {
        var path = LessRealBoothPaths.TryResolveEventMediaFile(_event.Id, _event.Name,
            _event.Experience.StartScreenRelativePath);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            OpeningBackgroundImage.Source = bmp;
            OpeningBackgroundImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Flow", $"start screen image: {ex.Message}");
        }
    }

    private void BeginPreRollOrCountdown()
    {
        if (_flowCts?.Token.IsCancellationRequested == true) return;
        var path = LessRealBoothPaths.TryResolveEventMediaFile(_event.Id, _event.Name,
            _event.Experience.PreRollVideoRelativePath);
        if (!_event.Experience.PlayPreRollBeforeEachPhoto || string.IsNullOrEmpty(path))
        {
            StartCountdown();
            return;
        }

        _preRollContinue = StartCountdown;
        PreRollOverlay.Visibility = Visibility.Visible;
        try
        {
            PreRollVideo.Stop();
            PreRollVideo.Source = new Uri(path, UriKind.Absolute);
            PreRollVideo.Position = TimeSpan.Zero;
            PreRollVideo.Play();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Flow", $"pre-roll: {ex.Message}");
            PreRollOverlay.Visibility = Visibility.Collapsed;
            _preRollContinue = null;
            StartCountdown();
        }
    }

    private void StopPreRollPlayback()
    {
        _preRollContinue = null;
        try
        {
            PreRollOverlay.Visibility = Visibility.Collapsed;
            PreRollVideo.Stop();
            PreRollVideo.Source = null;
        }
        catch
        {
            /* ignore */
        }
    }

    private void OnPreRollMediaEnded(object sender, RoutedEventArgs e)
    {
        PreRollOverlay.Visibility = Visibility.Collapsed;
        try
        {
            PreRollVideo.Stop();
            PreRollVideo.Source = null;
        }
        catch
        {
            /* ignore */
        }

        var next = _preRollContinue;
        _preRollContinue = null;
        next?.Invoke();
    }

    private void OnPreRollMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        RuntimeLog.Warn("Flow", $"pre-roll failed: {e.ErrorException?.Message}");
        PreRollOverlay.Visibility = Visibility.Collapsed;
        try
        {
            PreRollVideo.Stop();
            PreRollVideo.Source = null;
        }
        catch
        {
            /* ignore */
        }

        var next = _preRollContinue;
        _preRollContinue = null;
        next?.Invoke();
    }

    private void StartCountdown()
    {
        if (_flowCts == null || _flowCts.Token.IsCancellationRequested) return;
        var token = _flowCts.Token;
        _countdownCaptureScheduled = false;
        _prefocusArmed = false;
        var firstShot = _session?.PhotosTaken == 0;
        _countdownValue = firstShot ? FirstShotCountdownSeconds : NextShotCountdownSeconds;
        CountdownText.Visibility = Visibility.Visible;
        CountdownText.Text = _countdownValue.ToString();
        UpdateLiveSlotGuide();

        _stepTimer?.Stop();
        _stepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _stepTimer.Tick += (_, _) => CountdownTick(token);
        _stepTimer.Start();
    }

    private void CountdownTick(CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            _stepTimer?.Stop();
            return;
        }

        _countdownValue--;
        RuntimeLog.Info("Flow", $"countdown tick={_countdownValue}");
        if (_countdownValue == CaptureLeadSeconds && !_prefocusArmed)
        {
            _prefocusArmed = true;
            _ = SetPrefocusAsync(true, token);
        }
        if (_countdownValue > 0)
        {
            CountdownText.Text = _countdownValue.ToString();
            return;
        }

        _stepTimer?.Stop();
        if (_countdownCaptureScheduled)
            return;
        _countdownCaptureScheduled = true;
        CountdownText.Visibility = Visibility.Collapsed;
        RuntimeLog.Info("Flow", "capture trigger at zero");
        PlayFlashAndCapture();
    }

    private void PlayFlashAndCapture()
    {
        RuntimeLog.Info("Flow", "trigger capture (countdown reached zero)");
        var flash = new DoubleAnimationUsingKeyFrames();
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
        FlashOverlay.BeginAnimation(OpacityProperty, flash);

        CaptureAndAdvanceAsync();
    }

    private async void CaptureAndAdvanceAsync()
    {
        if (_session == null || _flowCts == null) return;
        var token = _flowCts.Token;
        if (token.IsCancellationRequested) return;

        var waitTurns = 0;
        while (Interlocked.CompareExchange(ref _captureBusy, 1, 0) != 0)
        {
            if (waitTurns++ > 1500)
            {
                RuntimeLog.Error("Flow", "capture gave up waiting for _captureBusy (stuck > ~60s)");
                return;
            }

            try
            {
                await Task.Delay(40, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        try
        {
            await Dispatcher.InvokeAsync(() =>
                ShotStatusLine.Text = "Taking photo via Sony bridge (127.0.0.1:18080)…");
            RuntimeLog.Info("Flow", $"capture start shot={_session.PhotosTaken + 1}");

            if (token.IsCancellationRequested) return;

            var shotNumber = _session.PhotosTaken + 1;
            CapturePullResult result;
            try
            {
                result = _workspace != null
                    ? await _workspace.TryPullCaptureFromBridgeAsync(shotNumber, token).ConfigureAwait(false)
                    : new CapturePullResult(false, null, "no_workspace");
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || _session == null) return;
                if (!result.Ok)
                {
                    RuntimeLog.Warn("Flow", $"capture failed shot={_session.PhotosTaken + 1} err={result.Error}");
                    _ = RetryCurrentShotAsync(token, result.Error);
                    return;
                }

                RuntimeLog.Info("Flow", $"capture saved shot={shotNumber} — showing {ShotReviewSeconds}s review");
                ShowShotReview(result.LocalPath!, shotNumber);
            });

        }
        finally
        {
            _ = SetPrefocusAsync(false, CancellationToken.None);
            Interlocked.Exchange(ref _captureBusy, 0);
        }
    }

    private async Task SetPrefocusAsync(bool enable, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _prefocusBusy, 1, 0) != 0)
            return;
        try
        {
            var url = $"http://127.0.0.1:18080/prefocus?on={(enable ? "1" : "0")}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await BridgeControlHttp.SendAsync(req, token).ConfigureAwait(false);
            RuntimeLog.Info("Flow", $"prefocus {(enable ? "on" : "off")} status={(int)res.StatusCode}");
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Flow", $"prefocus {(enable ? "on" : "off")} failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _prefocusBusy, 0);
        }
    }

    private void StopShotReview()
    {
        _shotReviewTimer?.Stop();
        _shotReviewTimer = null;
        _shotReviewShowing = false;
        ShotReviewOverlay.Visibility = Visibility.Collapsed;
        ShotReviewImage.Source = null;
        LivePreviewImage.Visibility = Visibility.Visible;
        LivePreviewHint.Visibility = Visibility.Visible;
        UpdateLiveSlotGuide();
    }

    private void ShowShotReview(string localPath, int shotNumber)
    {
        _pendingReviewShotNumber = shotNumber;
        _shotReviewShowing = true;
        StopLivePolling();

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(localPath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            ShotReviewImage.Source = bmp;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Flow", $"shot review image: {ex.Message}");
            ShotReviewImage.Source = null;
        }

        LivePreviewImage.Visibility = Visibility.Collapsed;
        LivePreviewHint.Visibility = Visibility.Collapsed;
        LiveSlotGuide.Visibility = Visibility.Collapsed;
        ShotReviewOverlay.Visibility = Visibility.Visible;
        ShotStatusLine.Text =
            $"Photo {shotNumber} of {_session?.Layout.ShotCount} — tap Retake or wait {ShotReviewSeconds:0} sec…";

        _shotReviewTimer?.Stop();
        _shotReviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ShotReviewSeconds) };
        _shotReviewTimer.Tick += (_, _) => AcceptPendingShotAndContinue();
        _shotReviewTimer.Start();
    }

    private void OnShotReviewRetakeClick(object sender, RoutedEventArgs e)
    {
        if (!_shotReviewShowing || _session == null || _flowCts == null) return;
        RuntimeLog.Info("Flow", $"retake shot={_pendingReviewShotNumber} (same slot, replaces file)");
        StopShotReview();
        StartLivePolling();
        ShotStatusLine.Text = $"Retaking photo {_pendingReviewShotNumber}…";
        UpdateLiveSlotGuide();
        StartCountdown();
    }

    private void AcceptPendingShotAndContinue()
    {
        if (!_shotReviewShowing || _session == null) return;
        StopShotReview();
        StartLivePolling();

        _session.PhotosTaken++;
        RuntimeLog.Info("Flow", $"photo accepted shot={_session.PhotosTaken}/{_session.Layout.ShotCount}");
        BuildShotDots();
        UpdateLimitsLine();
        ShotStatusLine.Text = $"Photo {_session.PhotosTaken} of {_session.Layout.ShotCount} saved.";

        if (_session.PhotosTaken >= _session.Layout.ShotCount)
            _ = FinalizeSessionAfterReviewAsync();
        else
            StartCurrentShot();
    }

    private async Task FinalizeSessionAfterReviewAsync()
    {
        try
        {
            await FinalizeSessionAndShowFinalAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("Flow", $"FinalizeSessionAndShowFinalAsync crashed: {ex}");
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        "The session finished, but building or showing the final print failed.\n\n" +
                        ex.Message +
                        "\n\nDetails are in Documents\\LessRealBooth\\logs\\runtime_*.log",
                        "BoothDesktop",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    FinalPreviewImage.Source = null;
                    FinalPreviewImage.Visibility = Visibility.Collapsed;
                    FinalPreviewPlaceholder.Visibility = Visibility.Visible;
                    FinalPreviewPlaceholder.Text =
                        "Could not build or show the composite. Check the runtime log.";
                    ShowPanel(PanelFinal);
                });
            }
            catch (Exception ex2)
            {
                RuntimeLog.Error("Flow", $"error UI after finalize crash: {ex2.Message}");
            }
        }
    }

    private async Task RetryCurrentShotAsync(CancellationToken token, string? error)
    {
        if (string.Equals(error, "cancelled", StringComparison.OrdinalIgnoreCase))
            return;

        _captureAutoRetryAttempt++;
        if (_captureAutoRetryAttempt > MaxCaptureAutoRetries)
        {
            var detail = FriendlyCaptureExplanation(error);
            var code = string.IsNullOrWhiteSpace(error) ? "unknown" : error;
            var again = await Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    $"Capture failed after {MaxCaptureAutoRetries} automatic retries.\n\n{detail}\n\nTechnical code: {code}\n\nTry this photo again?",
                    "Capture",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning));
            if (again != MessageBoxResult.Yes)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    TearDownCaptureFlow(clearSession: true);
                    ShowPanel(PanelLayoutPick);
                    ShotStatusLine.Text = "";
                });
                return;
            }

            _captureAutoRetryAttempt = 0;
            await Dispatcher.InvokeAsync(() =>
                ShotStatusLine.Text = "Trying again — same photo…");
            try
            {
                await Task.Delay(1600, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested)
                    StartCurrentShot(retryQuick: true);
            });
            return;
        }

        var friendly = FriendlyCaptureExplanation(error);
        var codeShort = string.IsNullOrWhiteSpace(error) ? "unknown" : (error.Length > 48 ? error[..48] + "…" : error);
        var delayMs = RetryDelayMsForAttempt(_captureAutoRetryAttempt);
        await Dispatcher.InvokeAsync(() =>
            ShotStatusLine.Text =
                $"{friendly} Retry {_captureAutoRetryAttempt}/{MaxCaptureAutoRetries} in {delayMs / 1000.0:0.#}s… ({codeShort})");
        RuntimeLog.Warn("Flow", $"retry scheduled attempt={_captureAutoRetryAttempt} delayMs={delayMs} reason={error ?? "unknown"}");

        try
        {
            await Task.Delay(delayMs, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (!token.IsCancellationRequested)
                StartCurrentShot(retryQuick: true);
        });
    }

    private static string FriendlyCaptureExplanation(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "The camera did not return a file.";
        if (error.StartsWith("invalid_json", StringComparison.OrdinalIgnoreCase))
            return "sony_bridge did not return JSON — wrong app on port 18080 or crashed mid-response.";
        if (error.StartsWith("release_", StringComparison.OrdinalIgnoreCase))
            return "Shutter command failed — camera busy, wrong mode, or USB/SDK contention with live view.";
        return error switch
        {
            "capture_timeout" => "Timed out waiting for the camera (still saving, USB slow, or bridge busy).",
            "camera_not_connected" => "Bridge reports the camera is not connected.",
            "capture_event_not_seen" => "No finished capture from the camera — wait for card write or try again.",
            "captured_but_file_missing" => "SDK saw a capture but the file was not found on disk.",
            "path_missing" => "Bridge path missing — file moved or not readable.",
            "no_workspace" => "Session folder missing (internal error).",
            "focus_not_locked" => "Autofocus did not lock (not used when nofocus=1).",
            _ => "Capture did not complete. Check sony_bridge window / Documents log."
        };
    }

    private static int RetryDelayMsForAttempt(int attempt) =>
        Math.Min(4500, 1400 + Math.Max(0, attempt - 1) * 450);

    private async Task FinalizeSessionAndShowFinalAsync()
    {
        // Entire finalize must run on the UI thread: DispatcherTimers, WPF compositor, and controls are all thread-affiliated.
        // CaptureAndAdvanceAsync often resumes on a pool thread after ConfigureAwait(false), so never touch timers/UI here directly.
        await Dispatcher.InvokeAsync(() =>
        {
            _flowCts?.Cancel();
            _stepTimer?.Stop();
            _stepTimer = null;
            _flowCts?.Dispose();
            _flowCts = null;
            Interlocked.Exchange(ref _captureBusy, 0);
            StopLivePolling();
            StopDiagnosticsTimer();

            string? finalPath = null;
            string? errMsg = null;
            if (_workspace != null)
            {
                var r = _workspace.TryComposeFinalPrint();
                if (r.Ok)
                {
                    finalPath = r.OutputPath;
                    RuntimeLog.Info("Flow", $"final composite written: {finalPath}");
                }
                else
                {
                    errMsg = r.Error;
                    RuntimeLog.Warn("Flow", $"final composite skipped: {errMsg}");
                }
            }
            else
                errMsg = "no_workspace";

            if (!string.IsNullOrEmpty(finalPath) && File.Exists(finalPath))
            {
                try
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.UriSource = new Uri(finalPath, UriKind.Absolute);
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    img.EndInit();
                    img.Freeze();
                    FinalPreviewImage.Source = img;
                    FinalPreviewImage.Visibility = Visibility.Visible;
                    FinalPreviewPlaceholder.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("Flow", $"final preview decode failed: {ex.Message}");
                    FinalPreviewImage.Source = null;
                    FinalPreviewImage.Visibility = Visibility.Collapsed;
                    FinalPreviewPlaceholder.Visibility = Visibility.Visible;
                    FinalPreviewPlaceholder.Text = "Composite saved but preview could not be loaded.";
                }
            }
            else
            {
                FinalPreviewImage.Source = null;
                FinalPreviewImage.Visibility = Visibility.Collapsed;
                FinalPreviewPlaceholder.Visibility = Visibility.Visible;
                FinalPreviewPlaceholder.Text = string.IsNullOrEmpty(errMsg)
                    ? "Composite not available."
                    : $"Composite skipped: {errMsg}";
            }

            ApplyFinalScreenPrintUi();
            ShowPanel(PanelFinal);
            TryAutoPrintAfterSessionComplete(finalPath);
        });
    }

    private static SessionLimitConfig BuildSessionLimits(PrintBehaviorSettings behavior) =>
        new()
        {
            MaxPhotosPerSession = 8,
            MaxPrintsPerEvent = behavior.LimitPrints ? behavior.MaxPrintsPerEvent : 9999,
            MaxPrintsPerSession = behavior.LimitPrints ? behavior.MaxPrintsPerSession : 99
        };

    private void ApplyFinalScreenPrintUi()
    {
        var showButton = GlobalSettingsService.Load().PrintBehavior.ShowPrintButton;
        FinalPrintLimitBanner.Visibility = Visibility.Collapsed;

        if (_workspace == null)
        {
            FinalPrintButton.Visibility = showButton ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        SyncActiveSessionPrintCountsIfSameFolder(_workspace.SessionRoot);
        UpdateLimitsLine();

        var quota = SessionPrintService.Evaluate(_event.Id, _event.Name, _workspace.SessionRoot);
        if (quota.LimitsEnabled && !quota.CanPrint)
        {
            FinalPrintButton.Visibility = Visibility.Collapsed;
            FinalPrintLimitBanner.Text = quota.UserMessage;
            FinalPrintLimitBanner.Visibility = Visibility.Visible;
            return;
        }

        FinalPrintButton.Visibility = showButton ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TryAutoPrintAfterSessionComplete(string? finalPath)
    {
        if (string.IsNullOrEmpty(finalPath) || _workspace == null) return;
        if (!GlobalSettingsService.Load().PrintBehavior.PrintAutomatically) return;

        if (!SessionPrintService.TryPrintSession(_event.Id, _event.Name, _workspace.SessionRoot, out var message))
            RuntimeLog.Warn("Print", $"auto-print failed: {message}");

        ApplyFinalScreenPrintUi();
    }

    private void SyncActiveSessionPrintCountsIfSameFolder(string sessionFolderAbs)
    {
        if (_session == null || _workspace == null) return;
        if (!string.Equals(_workspace.SessionRoot, sessionFolderAbs, StringComparison.OrdinalIgnoreCase))
            return;

        _session.PrintsThisSession = SessionPrintService.GetSessionPrintActionsUsed(sessionFolderAbs);
        _session.PrintsTotalEvent = EventPrintCounterService.LoadTotalPrints(_event.Id, _event.Name);
    }

    private void OnCancelSessionClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Cancel this session?\n\nTimers stop and no new captures are requested. " +
                "If a shot already started, the camera may still finish that one exposure.",
                "Cancel session",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        TearDownCaptureFlow(clearSession: true);
        ShowPanel(PanelLayoutPick);
        ShotStatusLine.Text = "";
    }

    private void OnNewGuestClick(object sender, RoutedEventArgs e)
    {
        TearDownCaptureFlow(clearSession: true);
        ShowPanel(PanelOpening);
    }

    private void OnShareClick(object sender, RoutedEventArgs e)
    {
        var extra = _workspace != null
            ? $"\r\n\r\nSession (captures + composite + session.json):\r\n{_workspace.SessionRoot}" +
              $"\r\n\r\nEvent folder (flat prints/, originals/, gallery_index.json for sharing station):\r\n{_workspace.EventRoot}"
            : "";
        MessageBox.Show(
            "Phase 4–5: QR + gallery link. The event folder lists every session (print + originals paths). For now, copy files manually if needed." + extra,
            "BoothDesktop", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnPrintClick(object sender, RoutedEventArgs e)
    {
        if (_workspace == null) return;
        TryGuestPrint(() =>
        {
            if (!SessionPrintService.TryPrintSession(_event.Id, _event.Name, _workspace.SessionRoot, out var message))
            {
                MessageBox.Show(message, "Print", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SyncActiveSessionPrintCountsIfSameFolder(_workspace.SessionRoot);
            ApplyFinalScreenPrintUi();
        });
    }

    private bool IsGuestPrintOnCooldown() => DateTime.UtcNow < _guestPrintCooldownUntilUtc;

    private void TryGuestPrint(Action print)
    {
        if (IsGuestPrintOnCooldown()) return;

        _guestPrintCooldownUntilUtc = DateTime.UtcNow.AddSeconds(1);
        SetGuestPrintButtonsEnabled(false);

        print();

        _guestPrintCooldownTimer?.Stop();
        _guestPrintCooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _guestPrintCooldownTimer.Tick += OnGuestPrintCooldownElapsed;
        _guestPrintCooldownTimer.Start();
    }

    private void OnGuestPrintCooldownElapsed(object? sender, EventArgs e)
    {
        _guestPrintCooldownTimer?.Stop();
        _guestPrintCooldownUntilUtc = DateTime.MinValue;
        SetGuestPrintButtonsEnabled(true);
    }

    private void SetGuestPrintButtonsEnabled(bool enabled)
    {
        if (FinalPrintButton.Visibility == Visibility.Visible)
            FinalPrintButton.IsEnabled = enabled;
        if (SessionDetailPrintButton.Visibility == Visibility.Visible)
            SessionDetailPrintButton.IsEnabled = enabled;
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Leave photobooth mode?", "BoothDesktop", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        TearDownCaptureFlow(clearSession: true);
        _nav.ExitPhotobooth();
    }
}
