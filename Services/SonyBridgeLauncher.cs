using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;

namespace BoothDesktop.Services;

/// <summary>
/// Starts native-poc sony_bridge.exe when BoothDesktop opens, if nothing is already serving /health.
/// Shuts down that process on app exit only if we started it.
/// </summary>
public static class SonyBridgeLauncher
{
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly object Gate = new();
    private static Process? _ownedProcess;
    private static bool _warnedMissing;
    private static DateTime _lastStartAttemptUtc;
    private static bool _startupBridgeResetDone;

    private const string HealthUrl = "http://127.0.0.1:18080/health";

    public static async Task EnsureStartedAsync()
    {
        lock (Gate)
        {
            if ((DateTime.UtcNow - _lastStartAttemptUtc) < TimeSpan.FromSeconds(5))
                return;
            _lastStartAttemptUtc = DateTime.UtcNow;
        }

        // Strict startup guard: always clear stale bridge instances once per Booth launch.
        // This prevents SDK/device conflicts where an old hidden sony_bridge keeps camera ownership.
        if (!_startupBridgeResetDone)
        {
            TryStopAllBridgeProcesses();
            _startupBridgeResetDone = true;
            await Task.Delay(500).ConfigureAwait(false);
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

        // Recovery path on repeated calls: if endpoint is still down, clear leftovers again before relaunch.
        TryStopAllBridgeProcesses();

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
            lock (Gate)
            {
                _ownedProcess = p;
            }
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

    /// <summary>For Phase A diagnostics strip on capture screen.</summary>
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
                return "Bridge process: running (started by Booth)";
            }
            catch
            {
                return "Bridge process: status unknown";
            }
        }
    }

    public static void StopIfWeStartedIt()
    {
        Process? p;
        lock (Gate)
        {
            p = _ownedProcess;
            _ownedProcess = null;
        }

        if (p == null || p.HasExited) return;
        try
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
        catch
        {
            /* ignore */
        }
        finally
        {
            p.Dispose();
        }
    }

    /// <summary>
    /// Kills every sony_bridge.exe (all copies). Uses taskkill first so orphaned / multi-instance
    /// bridges from prior runs or different folders are cleared reliably; then .NET Kill as backup.
    /// </summary>
    private static void TryStopAllBridgeProcesses()
    {
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
                    /* ignore one process and continue */
                }
                finally
                {
                    try { p.Dispose(); } catch { /* ignore */ }
                }
            }

            lock (Gate)
            {
                if (_ownedProcess != null)
                {
                    try
                    {
                        _ownedProcess.Refresh();
                        if (!_ownedProcess.HasExited)
                            _ownedProcess.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        /* ignore */
                    }
                    finally
                    {
                        try { _ownedProcess.Dispose(); } catch { /* ignore */ }
                        _ownedProcess = null;
                    }
                }
            }
        }
        catch
        {
            /* ignore stale cleanup failures */
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
            /* ignore — non-Windows or taskkill missing */
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

    /// <summary>Search common locations relative to BoothDesktop and repo layout.</summary>
    public static string? FindSonyBridgeExecutable()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Prefer native-poc Release output (Cr_Core.dll lives there). Do NOT prefer a lone sony_bridge.exe
        // next to BoothDesktop.exe — Windows will start it but fail with "Cr_Core.dll was not found".
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

    /// <summary>Various depths from bin/Release/net8.0-windows, _build_verify_out, etc.</summary>
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
