using System.Windows;
using System.Threading;
using BoothDesktop.Services;

namespace BoothDesktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        RuntimeLog.Info("App", "BoothDesktop startup");
        DispatcherUnhandledException += (_, args) =>
            RuntimeLog.Error("App", "DispatcherUnhandledException: " + args.Exception.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            RuntimeLog.Error("App", "UnhandledException: " + (args.ExceptionObject?.ToString() ?? "null"));

        var isPrimary = false;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\BoothDesktop.SingleInstance", out isPrimary);
        if (!isPrimary)
        {
            MessageBox.Show(
                "BoothDesktop is already running.\n\nClose the existing instance before launching another one.",
                "BoothDesktop",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        SessionEnding += (_, _) =>
        {
            RuntimeLog.Info("App", "Windows session ending — stopping Sony bridge");
            SonyBridgeLauncher.ShutdownAllForAppExit();
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RuntimeLog.Info("App", $"BoothDesktop exit code={e.ApplicationExitCode}");
        SonyBridgeLauncher.ShutdownAllForAppExit();
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
        catch
        {
            /* ignore */
        }
        base.OnExit(e);
    }
}

