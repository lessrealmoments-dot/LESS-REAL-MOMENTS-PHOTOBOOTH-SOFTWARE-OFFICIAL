using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;

namespace BoothDesktop.Services;

/// <summary>
/// Starts sony_bridge.exe for BoothDesktop and tears it down on app exit (graceful /shutdown, then force kill all).
/// Child bridge processes are tied to BoothDesktop via a Windows job object when possible.
/// </summary>
public static class SonyBridgeLauncher
{
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly object Gate = new();
    private static Process? _ownedProcess;
    private static bool _warnedMissing;
    private static DateTime _lastStartAttemptUtc;
    private static bool _startupBridgeResetDone;
    private static int _appExitCleanupStarted;

    private const string BridgeBaseUrl = "http://127.0.0.1:18080";
    private const string HealthUrl = BridgeBaseUrl + "/health";
    private const string ShutdownUrl = BridgeBaseUrl + "/shutdown";

    public static async Task EnsureStartedAsync()
    {
        lock (Gate)
        {
            if ((DateTime.UtcNow - _lastStartAttemptUtc) < TimeSpan.FromSeconds(5))
                return;
            _lastStartAttemptUtc = DateTime.UtcNow;
        }

        if (!_startupBridgeResetDone)
        {
            RuntimeLog.Info("Bridge", "startup: clearing stale sony_bridge processes");
            ForceStopAllBridgeProcesses();
            _startupBridgeResetDone = true;
            await Task.Delay(1500).ConfigureAwait(false);
        }

        if (await IsHealthyAsync().ConfigureAwait(false))
            return;

        var exe = FindSonyBridgeExecutable();
        if (string.IsNullOrEmpty(exe))
        {
            if (!_warnedMissing)
            {
                _warnedMissing = true;
                MessageBox.Show(
                    "Could not find a runnable sony_bridge.exe (with Sony SDK DLLs next to it).\n\n" +
                    "The bridge must sit in the same folder as Cr_Core.dll (from Camera Remote SDK), " +
                    "usually native-poc\\build\\Release after you build.\n\n" +
                    "If you copied only sony_bridge.exe next to BoothDesktop.exe, also copy Cr_Core.dll, " +
                    "monitor_protocol.dll, and monitor_protocol_pf.dll from that Release folder.\n\n" +
                    "Searched: native-poc\\…\\Release, then BoothDesktop folder only if Cr_Core.dll is there.",
                    "Sony bridge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return;
        }

        RuntimeLog.Info("Bridge", "recovery: clearing bridges before relaunch");
        ForceStopAllBridgeProcesses();

        var workDir = Path.GetDirectoryName(exe)!;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var p = Process.Start(psi);
            if (p == null) return;

            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                lock (Gate)
                {
                    if (ReferenceEquals(_ownedProcess, p))
                        _ownedProcess = null;
                }
            };

            BridgeParentJob.TryAssign(p);

            lock (Gate)
            {
                _ownedProcess = p;
            }

            RuntimeLog.Info("Bridge", $"started pid={p.Id} path={exe}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not start sony_bridge.exe:\n" + ex.Message,
                "Sony bridge",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250).ConfigureAwait(false);
            if (await IsHealthyAsync().ConfigureAwait(false))
                return;
        }

        MessageBox.Show(
            "sony_bridge.exe started but did not respond on http://127.0.0.1:18080/health in time.\n" +
            "Check the camera, USB mode, and that no other app is using the SDK session.",
            "Sony bridge",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>
    /// Call when BoothDesktop is closing (X, Alt+F4, Exit, logoff). Safe to call more than once.
    /// </summary>
    public static void ShutdownAllForAppExit()
    {
        if (Interlocked.Exchange(ref _appExitCleanupStarted, 1) != 0)
            return;

        RuntimeLog.Info("Bridge", "app exit: shutting down all sony_bridge processes");
        TryGracefulShutdown();
        ForceStopAllBridgeProcesses();
    }

    /// <summary>Obsolete name — forwards to <see cref="ShutdownAllForAppExit"/>.</summary>
    public static void StopIfWeStartedIt() => ShutdownAllForAppExit();

    public static string DescribeOwnedProcessStatus()
    {
        lock (Gate)
        {
            if (_ownedProcess == null)
                return "Bridge process: not started by Booth (manual or other app is OK)";
            try
            {
                _ownedProcess.Refresh();
                if (_ownedProcess.HasExited)
                    return $"Bridge process: exited (code {_ownedProcess.ExitCode}) — HTTP will be down";
                return $"Bridge process: running (pid {_ownedProcess.Id}, started by Booth)";
            }
            catch
            {
                return "Bridge process: status unknown";
            }
        }
    }

    private static void TryGracefulShutdown()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = client.PostAsync(ShutdownUrl, null).GetAwaiter().GetResult();
            RuntimeLog.Info("Bridge",
                $"graceful /shutdown status={(int)response.StatusCode} (old bridge without /shutdown is OK)");
            Thread.Sleep(900);
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Bridge", $"graceful /shutdown skipped: {ex.Message}");
        }
    }

    private static void ForceStopAllBridgeProcesses()
    {
        lock (Gate)
        {
            _ownedProcess = null;
        }

        TryTaskKillAllSonyBridgeExe();
        Thread.Sleep(1200);

        try
        {
            foreach (var p in Process.GetProcessesByName("sony_bridge"))
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(3000);
                    }
                }
                catch
                {
                    /* ignore one process */
                }
                finally
                {
                    try { p.Dispose(); } catch { /* ignore */ }
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Bridge", $"ForceStopAllBridgeProcesses: {ex.Message}");
        }
    }

    private static void TryTaskKillAllSonyBridgeExe()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = "/F /IM sony_bridge.exe /T",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            p?.WaitForExit(20000);
        }
        catch
        {
            /* ignore */
        }
    }

    private static async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var r = await HealthClient.GetAsync(HealthUrl).ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindSonyBridgeExecutable()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidates = new List<string>();
        foreach (var rel in NativeBridgeRelativeTries)
            candidates.Add(Path.GetFullPath(Path.Combine(baseDir, rel)));
        candidates.Add(Path.Combine(baseDir, "sony_bridge.exe"));

        foreach (var full in candidates)
        {
            if (!File.Exists(full)) continue;
            if (!HasSonySdkBesideBridge(full)) continue;
            return full;
        }

        return null;
    }

    private static readonly string[] NativeBridgeRelativeTries =
    [
        Path.Combine("..", "..", "..", "..", "native-poc", "build", "Release", "sony_bridge.exe"),
        Path.Combine("..", "..", "..", "..", "native-poc", "build_fresh", "Release", "sony_bridge.exe"),
        Path.Combine("..", "..", "..", "native-poc", "build", "Release", "sony_bridge.exe"),
        Path.Combine("..", "..", "..", "native-poc", "build_fresh", "Release", "sony_bridge.exe"),
        Path.Combine("..", "..", "native-poc", "build", "Release", "sony_bridge.exe"),
        Path.Combine("..", "..", "native-poc", "build_fresh", "Release", "sony_bridge.exe"),
    ];

    private static bool HasSonySdkBesideBridge(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(dir)) return false;
        return File.Exists(Path.Combine(dir, "Cr_Core.dll"));
    }
}
