using System.IO;
using System.Printing;
using System.Windows;
using BoothDesktop.Models;
using BoothDesktop.Services;
using Microsoft.Win32;

namespace BoothDesktop.Views;

public partial class GlobalSettingsWindow : Window
{
    private const string PrinterSystemDefault = "(System default)";
    private GlobalAppSettings _draft = new();

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

        PrinterCombo.Items.Clear();
        PrinterCombo.Items.Add(PrinterSystemDefault);
        try
        {
            var server = new LocalPrintServer();
            foreach (var q in server.GetPrintQueues())
            {
                if (!string.IsNullOrWhiteSpace(q.Name))
                    PrinterCombo.Items.Add(q.Name);
            }
        }
        catch
        {
            /* ignore print-enumeration failures */
        }

        if (!string.IsNullOrWhiteSpace(_draft.PreferredPrinterName)
            && PrinterCombo.Items.Contains(_draft.PreferredPrinterName))
            PrinterCombo.SelectedItem = _draft.PreferredPrinterName;
        else
            PrinterCombo.SelectedItem = PrinterSystemDefault;

        RefreshStorageHint();
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
        var selectedPrinter = PrinterCombo.SelectedItem as string;
        var settings = new GlobalAppSettings
        {
            StorageRootPath = string.IsNullOrWhiteSpace(StorageRootText.Text) ? null : StorageRootText.Text.Trim(),
            PreferredPrinterName = selectedPrinter == PrinterSystemDefault ? null : selectedPrinter
        };

        if (!GlobalSettingsService.TrySave(settings, out var error))
        {
            MessageBox.Show(this, "Could not save global settings.\n" + error, "Global settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
