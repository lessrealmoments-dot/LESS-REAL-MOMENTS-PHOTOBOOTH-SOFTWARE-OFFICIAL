using System.Windows;
using BoothDesktop.Models;
using BoothDesktop.Services;
using BoothDesktop.Views;

namespace BoothDesktop;

public partial class MainWindow : Window, INavigationHost
{
    public MainWindow()
    {
        InitializeComponent();
        Shell.Content = new EventsView(this);
        Loaded += MainWindow_OnLoaded;
        Closing += (_, _) => SonyBridgeLauncher.ShutdownAllForAppExit();
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_OnLoaded;
        await SonyBridgeLauncher.EnsureStartedAsync();
    }

    public void EnterPhotobooth(BoothEventSummary boothEvent)
    {
        Shell.Content = new PhotoboothFlowView(boothEvent, this);
        WindowState = WindowState.Maximized;
    }

    public void ExitPhotobooth()
    {
        Shell.Content = new EventsView(this);
        WindowState = WindowState.Normal;
    }
}
