using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BoothDesktop.Services;

/// <summary>Phase A: probe /health, /live.jpg, and SHM for operator-visible status.</summary>
public static class BridgeDiagnosticsService
{
    private static readonly HttpClient ProbeHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static async Task<BridgeDiagnosticsSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var processLine = SonyBridgeLauncher.DescribeOwnedProcessStatus();

        var healthLine = await ProbeHealthAsync(cancellationToken).ConfigureAwait(false);
        var liveLine = await ProbeLiveAsync(cancellationToken).ConfigureAwait(false);

        var shmOk = SonyBridgeSharedMemoryReader.TryReadLatestJpeg(out _, out var fid, out var shmErr);
        var shmLine = shmOk ? $"SHM: OK · last frameId={fid}" : $"SHM: {shmErr ?? "no data"}";

        return new BridgeDiagnosticsSnapshot(processLine, healthLine, liveLine, shmLine);
    }

    private static async Task<string> ProbeHealthAsync(CancellationToken ct)
    {
        try
        {
            using var r = await ProbeHttp.GetAsync("http://127.0.0.1:18080/health", ct).ConfigureAwait(false);
            var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var connected = "?";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("connected", out var c))
                    connected = c.GetBoolean() ? "true" : "false";
            }
            catch
            {
                connected = "parse?";
            }

            return $"Health: HTTP {(int)r.StatusCode} · connected={connected} · {Truncate(body, 80)}";
        }
        catch (Exception ex)
        {
            return $"Health: unreachable — {ex.Message}";
        }
    }

    private static async Task<string> ProbeLiveAsync(CancellationToken ct)
    {
        try
        {
            var url = $"http://127.0.0.1:18080/live.jpg?diag={DateTime.UtcNow.Ticks}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            using var r = await ProbeHttp.SendAsync(req, ct).ConfigureAwait(false);
            var bytes = await r.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (r.IsSuccessStatusCode)
                return $"live.jpg: HTTP {(int)r.StatusCode} · {bytes.Length} bytes (JPEG)";

            var txt = Encoding.UTF8.GetString(bytes);
            return $"live.jpg: HTTP {(int)r.StatusCode} · {Truncate(txt, 100)}";
        }
        catch (Exception ex)
        {
            return $"live.jpg: failed — {ex.Message}";
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var one = s.ReplaceLineEndings(" ");
        return one.Length <= max ? one : one[..max] + "…";
    }
}

public sealed record BridgeDiagnosticsSnapshot(
    string BridgeProcessLine,
    string HealthLine,
    string LiveLine,
    string ShmLine)
{
    public string ToDisplayText() =>
        $"{BridgeProcessLine}\n{HealthLine}\n{LiveLine}\n{ShmLine}";
}
