using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using BoothDesktop.Controls;
using BoothDesktop.Models;
using BoothDesktop.Services;
using Microsoft.Win32;

namespace BoothDesktop.Views;

public partial class GlobalSettingsWindow : Window
{
    private const string PrinterSystemDefault = "(System default)";
    private GlobalAppSettings _draft = new();
    private bool _suppressQueueChange;
    private bool _suppressAlignmentUiEvents;
    private List<PrintAlignmentSample> _alignmentSamples = [];
    private string? _testPatternPreviewPath;
    private PrintAlignmentSample? _customSample;

    public GlobalSettingsWindow(Window owner)
    {
        Owner = owner;
        InitializeComponent();
        Loaded += (_, _) => LoadDraft();
    }

    private void LoadDraft()
    {
        _draft = GlobalSettingsService.Load();
        StorageRootText.Text = _draft.StorageRootPath ?? "";

        _suppressQueueChange = true;
        PopulatePrinterCombo(Printer1Combo, _draft.Printer1.PrinterName);
        PopulatePrinterCombo(Printer2Combo, _draft.Printer2.PrinterName);
        _suppressQueueChange = false;

        Printer1CopiesText.Text = _draft.Printer1.Copies.ToString();
        Printer2CopiesText.Text = _draft.Printer2.Copies.ToString();
        Printer1ProfileText.Text = _draft.Printer1.ProfileLabel ?? "";
        Printer2ProfileText.Text = _draft.Printer2.ProfileLabel ?? "";

        WireAlignmentControls(1);
        WireAlignmentControls(2);
        LoadAlignmentUiFromDraft(1);
        LoadAlignmentUiFromDraft(2);
        Printer2FollowPrinter1AlignCheck.IsChecked = _draft.Printer2.FollowPrinter1Alignment;
        UpdatePrinter2AlignmentPanelEnabled();

        RefreshPrefStatus(1);
        RefreshPrefStatus(2);

        var pb = _draft.PrintBehavior;
        PrintAutoCheck.IsChecked = pb.PrintAutomatically;
        ShowPrintButtonCheck.IsChecked = pb.ShowPrintButton;
        PrintBothCheck.IsChecked = pb.PrintToBothPrinters;
        LimitPrintsCheck.IsChecked = pb.LimitPrints;
        MaxPrintsEventText.Text = pb.MaxPrintsPerEvent.ToString();
        MaxPrintsSessionText.Text = pb.MaxPrintsPerSession.ToString();
        PrintDialogMaxText.Text = pb.PrintDialogMaxCopies.ToString();
        PopulateSharpeningCombo(pb.PrintSharpening);
        UpdateLimitPrintsPanelEnabled();

        PopulateAlignmentSamples();
        PopulatePaperOrientationCombo();
        RefreshAlignmentPreview();

        RefreshStorageHint();
    }

    private void PopulateSharpeningCombo(string? selected)
    {
        SharpeningCombo.Items.Clear();
        foreach (var s in new[] { "None", "Low", "Medium", "High" })
            SharpeningCombo.Items.Add(s);
        SharpeningCombo.SelectedItem = selected is "None" or "Low" or "Medium" or "High" ? selected : "Medium";
    }

    private void WireAlignmentControls(int slot)
    {
        var scaleSlider = slot == 1 ? Printer1AlignScaleSlider : Printer2AlignScaleSlider;
        var scaleText = slot == 1 ? Printer1AlignScaleText : Printer2AlignScaleText;
        var hSlider = slot == 1 ? Printer1AlignHorizontalSlider : Printer2AlignHorizontalSlider;
        var hText = slot == 1 ? Printer1AlignHorizontalText : Printer2AlignHorizontalText;
        var vSlider = slot == 1 ? Printer1AlignVerticalSlider : Printer2AlignVerticalSlider;
        var vText = slot == 1 ? Printer1AlignVerticalText : Printer2AlignVerticalText;

        scaleSlider.ValueChanged += (_, _) => OnAlignmentSliderChanged(scaleSlider, scaleText);
        hSlider.ValueChanged += (_, _) => OnAlignmentSliderChanged(hSlider, hText);
        vSlider.ValueChanged += (_, _) => OnAlignmentSliderChanged(vSlider, vText);

        scaleText.LostFocus += (_, _) => OnAlignmentTextLostFocus(scaleText, scaleSlider, 50, 150);
        hText.LostFocus += (_, _) => OnAlignmentTextLostFocus(hText, hSlider, -200, 200);
        vText.LostFocus += (_, _) => OnAlignmentTextLostFocus(vText, vSlider, -200, 200);
    }

    private void OnAlignmentSliderChanged(Slider slider, TextBox text)
    {
        if (_suppressAlignmentUiEvents) return;
        SyncSliderToText(slider, text, "F0");
        RefreshAlignmentPreview();
    }

    private void OnAlignmentTextLostFocus(TextBox text, Slider slider, int min, int max)
    {
        if (_suppressAlignmentUiEvents) return;
        SyncTextToSlider(text, slider, min, max);
        RefreshAlignmentPreview();
    }

    private void LoadAlignmentUiFromDraft(int slot)
    {
        var settings = slot == 1 ? _draft.Printer1 : _draft.Printer2;
        if (slot == 2 && _draft.Printer2.FollowPrinter1Alignment)
            settings = _draft.Printer1;

        var scaleSlider = slot == 1 ? Printer1AlignScaleSlider : Printer2AlignScaleSlider;
        var scaleText = slot == 1 ? Printer1AlignScaleText : Printer2AlignScaleText;
        var hSlider = slot == 1 ? Printer1AlignHorizontalSlider : Printer2AlignHorizontalSlider;
        var hText = slot == 1 ? Printer1AlignHorizontalText : Printer2AlignHorizontalText;
        var vSlider = slot == 1 ? Printer1AlignVerticalSlider : Printer2AlignVerticalSlider;
        var vText = slot == 1 ? Printer1AlignVerticalText : Printer2AlignVerticalText;

        _suppressAlignmentUiEvents = true;
        try
        {
            scaleSlider.Value = settings.AlignmentScalePercent;
            hSlider.Value = settings.AlignmentOffsetXHundredths;
            vSlider.Value = settings.AlignmentOffsetYHundredths;
            SyncSliderToText(scaleSlider, scaleText, "F0");
            SyncSliderToText(hSlider, hText, "F0");
            SyncSliderToText(vSlider, vText, "F0");
        }
        finally
        {
            _suppressAlignmentUiEvents = false;
        }

    }

    /// <summary>Reads slider values into draft (source of truth for save/print).</summary>
    private void CommitAlignmentToDraft()
    {
        CommitAlignmentToDraft(1);
        _draft.Printer2.FollowPrinter1Alignment = Printer2FollowPrinter1AlignCheck.IsChecked == true;
        if (_draft.Printer2.FollowPrinter1Alignment)
        {
            _draft.Printer2.AlignmentScalePercent = _draft.Printer1.AlignmentScalePercent;
            _draft.Printer2.AlignmentOffsetXHundredths = _draft.Printer1.AlignmentOffsetXHundredths;
            _draft.Printer2.AlignmentOffsetYHundredths = _draft.Printer1.AlignmentOffsetYHundredths;
            LoadAlignmentUiFromDraft(2);
        }
        else
        {
            CommitAlignmentToDraft(2);
        }

    }

    private void CommitAlignmentToDraft(int slot)
    {
        var settings = slot == 1 ? _draft.Printer1 : _draft.Printer2;
        var scaleSlider = slot == 1 ? Printer1AlignScaleSlider : Printer2AlignScaleSlider;
        var hSlider = slot == 1 ? Printer1AlignHorizontalSlider : Printer2AlignHorizontalSlider;
        var vSlider = slot == 1 ? Printer1AlignVerticalSlider : Printer2AlignVerticalSlider;

        settings.AlignmentScalePercent = (int)Math.Round(scaleSlider.Value);
        settings.AlignmentOffsetXHundredths = (int)Math.Round(hSlider.Value);
        settings.AlignmentOffsetYHundredths = (int)Math.Round(vSlider.Value);
        PrinterAlignmentResolver.NormalizeSlot(settings);
    }

    private EffectivePrinterAlignment GetAlignmentFromUiForPreview(int slot)
    {
        if (slot == 2 && Printer2FollowPrinter1AlignCheck.IsChecked == true)
        {
            return new EffectivePrinterAlignment
            {
                ScalePercent = (int)Math.Round(Printer1AlignScaleSlider.Value),
                OffsetXHundredths = (int)Math.Round(Printer1AlignHorizontalSlider.Value),
                OffsetYHundredths = (int)Math.Round(Printer1AlignVerticalSlider.Value),
                FollowedPrinter1 = true
            };
        }

        var scaleSlider = slot == 1 ? Printer1AlignScaleSlider : Printer2AlignScaleSlider;
        var hSlider = slot == 1 ? Printer1AlignHorizontalSlider : Printer2AlignHorizontalSlider;
        var vSlider = slot == 1 ? Printer1AlignVerticalSlider : Printer2AlignVerticalSlider;
        return new EffectivePrinterAlignment
        {
            ScalePercent = (int)Math.Round(scaleSlider.Value),
            OffsetXHundredths = (int)Math.Round(hSlider.Value),
            OffsetYHundredths = (int)Math.Round(vSlider.Value),
            FollowedPrinter1 = false
        };
    }

    private static void SyncSliderToText(Slider slider, TextBox text, string format) =>
        text.Text = slider.Value.ToString(format);

    private static void SyncTextToSlider(TextBox text, Slider slider, int min, int max)
    {
        var v = ParseAlignInt(text.Text, (int)slider.Value, min, max);
        slider.Value = v;
        text.Text = v.ToString("F0");
    }

    private static int ParseAlignInt(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse(text?.Trim(), out var v))
            v = fallback;
        return Math.Clamp(v, min, max);
    }

    private void OnPrinter2FollowPrinter1Changed(object sender, RoutedEventArgs e)
    {
        UpdatePrinter2AlignmentPanelEnabled();
        RefreshAlignmentPreview();
    }

    private void OnPrinterTabChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshAlignmentPreview();

    private void OnAlignSampleChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshAlignmentPreview();

    private void OnPaperOrientationChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshAlignmentPreview();

    private void OnBrowseAlignSampleClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a composite image for alignment preview",
            Filter = "Images|*.png;*.jpg;*.jpeg|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        var w = 1200;
        var h = 1800;
        try
        {
            using var img = System.Drawing.Image.FromFile(dlg.FileName);
            w = img.Width;
            h = img.Height;
        }
        catch
        {
            /* keep defaults */
        }

        _customSample = new PrintAlignmentSample
        {
            DisplayName = Path.GetFileName(dlg.FileName),
            ImagePath = dlg.FileName,
            LayoutId = null,
            PixelWidth = w,
            PixelHeight = h
        };

        var list = new List<PrintAlignmentSample> { _customSample };
        list.AddRange(_alignmentSamples.Where(s => s.ImagePath != _customSample.ImagePath));
        AlignSampleCombo.ItemsSource = list;
        AlignSampleCombo.SelectedIndex = 0;
        RefreshAlignmentPreview();
    }

    private void PopulateAlignmentSamples()
    {
        _alignmentSamples = PrintAlignmentSampleService.FindRecentComposites().ToList();
        AlignSampleCombo.ItemsSource = _alignmentSamples;
        AlignSampleCombo.DisplayMemberPath = nameof(PrintAlignmentSample.DisplayName);
        AlignSampleCombo.SelectedIndex = _alignmentSamples.Count > 1 ? 1 : 0;
    }

    private void PopulatePaperOrientationCombo()
    {
        PaperOrientationCombo.ItemsSource = new[]
        {
            new PaperOrientationChoice("Portrait — paper tall (4×6 vertical)", true),
            new PaperOrientationChoice("Landscape — paper wide (4×6 horizontal)", false)
        };
        PaperOrientationCombo.DisplayMemberPath = nameof(PaperOrientationChoice.Label);
        PaperOrientationCombo.SelectedIndex = 0;
    }

    private PrintAlignmentSample GetSelectedSample()
    {
        if (AlignSampleCombo.SelectedItem is PrintAlignmentSample sample)
            return sample;
        return PrintAlignmentSampleService.TestPatternSample;
    }

    private bool GetPaperOrientationPortrait() =>
        PaperOrientationCombo.SelectedItem is PaperOrientationChoice c ? c.Portrait : true;

    private string EnsureTestPatternPreviewFile()
    {
        if (!string.IsNullOrEmpty(_testPatternPreviewPath) && File.Exists(_testPatternPreviewPath))
            return _testPatternPreviewPath;

        _testPatternPreviewPath = Path.Combine(Path.GetTempPath(), "lrb_align_preview_test.png");
        using var bmp = BoothPrintTestImage.CreatePortrait4x6("Preview");
        bmp.Save(_testPatternPreviewPath, ImageFormat.Png);
        return _testPatternPreviewPath;
    }

    private void RefreshAlignmentPreview()
    {
        if (!IsLoaded) return;

        var slot = PrinterTabs.SelectedIndex == 1 ? 2 : 1;
        var sample = GetSelectedSample();
        var portraitPaper = GetPaperOrientationPortrait();
        var alignment = GetAlignmentFromUiForPreview(slot);
        var imagePath = string.IsNullOrEmpty(sample.ImagePath)
            ? EnsureTestPatternPreviewFile()
            : sample.ImagePath;
        float dpiUsed = 300;
        SizeF content;
        if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
        {
            using var img = System.Drawing.Image.FromFile(imagePath);
            (content, dpiUsed) = BoothPrintLayout.ResolveContentSizeDisplayUnits(img, sample.LayoutId);
        }
        else
        {
            content = new SizeF(400, 600);
        }

        var layoutOrient = sample.PixelHeight > sample.PixelWidth
            ? "Portrait"
            : sample.PixelWidth > sample.PixelHeight ? "Landscape" : "Square";
        var paperOrient = portraitPaper ? "Portrait" : "Landscape";
        var slotLabel = slot == 2 && Printer2FollowPrinter1AlignCheck.IsChecked == true
            ? "Printer 2 (using Printer 1 alignment)"
            : $"Printer {slot}";

        var state = new PrintAlignmentPreviewState
        {
            SummaryText = $"{slotLabel} — {layoutOrient} on {paperOrient} paper",
            DetailText =
                $"Print size {content.Width / 100f:F1}×{content.Height / 100f:F1} in @ {dpiUsed:F0} DPI · " +
                $"Adjust {alignment.ScalePercent}% · X {alignment.OffsetXHundredths} · Y {alignment.OffsetYHundredths}",
            Alignment = alignment,
            PaperWidthHundredths = portraitPaper ? 400 : 600,
            PaperHeightHundredths = portraitPaper ? 600 : 400,
            ContentWidthHundredths = content.Width,
            ContentHeightHundredths = content.Height,
            SampleImagePath = imagePath
        };

        Printer1AlignPreview.Update(state);
        Printer2AlignPreview.Update(state);
    }

    private sealed class PaperOrientationChoice(string label, bool portrait)
    {
        public string Label { get; } = label;
        public bool Portrait { get; } = portrait;
    }

    private void UpdatePrinter2AlignmentPanelEnabled()
    {
        var follow = Printer2FollowPrinter1AlignCheck.IsChecked == true;
        Printer2AlignmentPanel.IsEnabled = !follow;
        Printer2AlignmentPanel.Opacity = follow ? 0.45 : 1;
        if (follow)
            LoadAlignmentUiFromDraft(2);
    }

    private void OnPrintTestPage1Click(object sender, RoutedEventArgs e) => PrintAlignmentTest(1);

    private void OnPrintTestPage2Click(object sender, RoutedEventArgs e) => PrintAlignmentTest(2);

    private void PrintAlignmentTest(int slot)
    {
        CommitAlignmentToDraft();

        var queue = slot == 1 ? ReadPrinterName(Printer1Combo) : ReadPrinterName(Printer2Combo);
        if (string.IsNullOrEmpty(queue))
        {
            MessageBox.Show(this,
                $"Choose a printer queue for printer {slot} before printing a test page.",
                "Print test", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = BoothPrintService.TryPrintAlignmentTestPage(slot, _draft);
        MessageBox.Show(this, result.Message, "Print test",
            MessageBoxButton.OK,
            result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnLimitPrintsCheckChanged(object sender, RoutedEventArgs e) => UpdateLimitPrintsPanelEnabled();

    private void UpdateLimitPrintsPanelEnabled()
    {
        LimitPrintsPanel.IsEnabled = LimitPrintsCheck.IsChecked == true;
        LimitPrintsPanel.Opacity = LimitPrintsPanel.IsEnabled ? 1 : 0.45;
    }

    private static void PopulatePrinterCombo(ComboBox combo, string? selectedName)
    {
        combo.Items.Clear();
        combo.Items.Add(PrinterSystemDefault);
        try
        {
            var server = new LocalPrintServer();
            foreach (var q in server.GetPrintQueues())
            {
                if (!string.IsNullOrWhiteSpace(q.Name))
                    combo.Items.Add(q.Name);
            }
        }
        catch
        {
            /* ignore */
        }

        if (!string.IsNullOrWhiteSpace(selectedName) && combo.Items.Contains(selectedName))
            combo.SelectedItem = selectedName;
        else
            combo.SelectedItem = PrinterSystemDefault;
    }

    private void RefreshPrefStatus(int slot)
    {
        var settings = slot == 2 ? _draft.Printer2 : _draft.Printer1;
        var status = slot == 2 ? Printer2PrefStatus : Printer1PrefStatus;
        var queue = settings.PrinterName ?? "(system default)";

        if (settings.HasDriverPreferences)
        {
            var label = string.IsNullOrWhiteSpace(settings.ProfileLabel)
                ? "Driver preferences saved"
                : settings.ProfileLabel;
            status.Text =
                $"{label} — applies when layouts print to printer {slot} ({queue}).";
            status.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text")!;
        }
        else
        {
            status.Text =
                $"No saved driver preferences for printer {slot}. " +
                "Click Configure printer… to set HiTi 4×6 vs 2-up strip (same queue allowed).";
            status.Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextMuted")!;
        }
    }

    private void OnPrinter1QueueChanged(object sender, SelectionChangedEventArgs e) =>
        OnPrinterQueueChanged(1);

    private void OnPrinter2QueueChanged(object sender, SelectionChangedEventArgs e) =>
        OnPrinterQueueChanged(2);

    private void OnPrinterQueueChanged(int slot)
    {
        if (_suppressQueueChange) return;

        var settings = slot == 2 ? _draft.Printer2 : _draft.Printer1;
        var hadPrefs = settings.HasDriverPreferences;
        settings.PrinterName = ReadPrinterName(slot == 2 ? Printer2Combo : Printer1Combo);
        if (hadPrefs)
        {
            settings.ClearDriverPreferences();
            MessageBox.Show(this,
                $"Printer {slot}: queue changed — saved driver preferences were cleared. " +
                "Use Configure printer… again for this slot.",
                "Print setup", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        RefreshPrefStatus(slot);
    }

    private void OnConfigurePrinter1Click(object sender, RoutedEventArgs e) =>
        ConfigureSlot(1);

    private void OnConfigurePrinter2Click(object sender, RoutedEventArgs e) =>
        ConfigureSlot(2);

    private void ConfigureSlot(int slot)
    {
        var settings = slot == 2 ? _draft.Printer2 : _draft.Printer1;
        var combo = slot == 2 ? Printer2Combo : Printer1Combo;
        var profileBox = slot == 2 ? Printer2ProfileText : Printer1ProfileText;

        var queue = ReadPrinterName(combo);
        if (string.IsNullOrEmpty(queue))
        {
            MessageBox.Show(this,
                $"Choose a printer queue for printer {slot} before configuring preferences.",
                "Print setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        settings.PrinterName = queue;
        settings.ProfileLabel = string.IsNullOrWhiteSpace(profileBox.Text) ? null : profileBox.Text.Trim();

        var existing = settings.GetDriverPreferencesBytes();
        var result = PrinterDriverPreferencesService.TryConfigure(this, queue, existing,
            out var updated, out var message);

        if (result == PrinterDriverPreferencesService.ConfigureResult.Cancelled)
            return;

        if (result != PrinterDriverPreferencesService.ConfigureResult.Ok || updated == null)
        {
            MessageBox.Show(this, message, "Print setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        settings.SetDriverPreferencesBytes(updated);
        RefreshPrefStatus(slot);
        MessageBox.Show(this,
            $"Printer {slot} ({queue}): driver preferences saved.\n\n" +
            "Tip: use slot 1 for 4×6 4R and slot 2 for 2-up / strip on the same HiTi if needed.",
            "Print setup", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnClearPrinter1Click(object sender, RoutedEventArgs e) => ClearSlot(1);

    private void OnClearPrinter2Click(object sender, RoutedEventArgs e) => ClearSlot(2);

    private void ClearSlot(int slot)
    {
        var settings = slot == 2 ? _draft.Printer2 : _draft.Printer1;
        settings.ClearDriverPreferences();
        RefreshPrefStatus(slot);
    }

    private void RefreshStorageHint()
    {
        if (string.IsNullOrWhiteSpace(StorageRootText.Text))
        {
            StorageHintText.Text = $"Default: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), LessRealBoothPaths.RootFolderName)}";
            return;
        }

        try
        {
            StorageHintText.Text = Path.GetFullPath(StorageRootText.Text.Trim());
        }
        catch
        {
            StorageHintText.Text = "Path is invalid.";
        }
    }

    private void OnBrowseStorageClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose LessRealBooth storage root",
            Multiselect = false
        };
        if (dlg.ShowDialog() != true) return;
        StorageRootText.Text = dlg.FolderName;
        RefreshStorageHint();
    }

    private void OnUseDefaultStorageClick(object sender, RoutedEventArgs e)
    {
        StorageRootText.Text = "";
        RefreshStorageHint();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseCopies(Printer1CopiesText.Text, out var copies1, out var err1))
        {
            MessageBox.Show(this, err1, "Printer 1", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseCopies(Printer2CopiesText.Text, out var copies2, out var err2))
        {
            MessageBox.Show(this, err2, "Printer 2", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _draft.Printer1.PrinterName = ReadPrinterName(Printer1Combo);
        _draft.Printer2.PrinterName = ReadPrinterName(Printer2Combo);
        _draft.Printer1.Copies = copies1;
        _draft.Printer2.Copies = copies2;
        _draft.Printer1.ProfileLabel = string.IsNullOrWhiteSpace(Printer1ProfileText.Text)
            ? null
            : Printer1ProfileText.Text.Trim();
        _draft.Printer2.ProfileLabel = string.IsNullOrWhiteSpace(Printer2ProfileText.Text)
            ? null
            : Printer2ProfileText.Text.Trim();

        CommitAlignmentToDraft();

        if (!TryParsePositiveInt(MaxPrintsEventText.Text, 1, 9999, out var maxEvent, out var errEvent))
        {
            MessageBox.Show(this, errEvent, "Print limits", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParsePositiveInt(MaxPrintsSessionText.Text, 1, 99, out var maxSession, out var errSession))
        {
            MessageBox.Show(this, errSession, "Print limits", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParsePositiveInt(PrintDialogMaxText.Text, 1, 99, out var maxDlg, out var errDlg))
        {
            MessageBox.Show(this, errDlg, "Guest print max", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _draft.PrintBehavior = new PrintBehaviorSettings
        {
            PrintAutomatically = PrintAutoCheck.IsChecked == true,
            ShowPrintButton = ShowPrintButtonCheck.IsChecked == true,
            PrintToBothPrinters = PrintBothCheck.IsChecked == true,
            LimitPrints = LimitPrintsCheck.IsChecked == true,
            MaxPrintsPerEvent = maxEvent,
            MaxPrintsPerSession = maxSession,
            PrintDialogMaxCopies = maxDlg,
            PrintSharpening = SharpeningCombo.SelectedItem as string ?? "Medium"
        };

        if (!GlobalSettingsService.TrySave(_draft, out var error))
        {
            MessageBox.Show(this, "Could not save global settings.\n" + error, "Global settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private static string? ReadPrinterName(ComboBox combo)
    {
        var selected = combo.SelectedItem as string;
        return selected == PrinterSystemDefault ? null : selected;
    }

    private static bool TryParseCopies(string? text, out int copies, out string error)
    {
        copies = 1;
        error = "";
        if (!int.TryParse(text?.Trim(), out copies) || copies < 1 || copies > 99)
        {
            error = "Copies must be a number from 1 to 99.";
            return false;
        }

        return true;
    }

    private static bool TryParsePositiveInt(string? text, int min, int max, out int value, out string error)
    {
        value = min;
        error = "";
        if (!int.TryParse(text?.Trim(), out value) || value < min || value > max)
        {
            error = $"Enter a whole number from {min} to {max}.";
            return false;
        }

        return true;
    }
}
