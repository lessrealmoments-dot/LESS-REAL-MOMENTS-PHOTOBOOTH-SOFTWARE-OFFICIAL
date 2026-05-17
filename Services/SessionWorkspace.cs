using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoothDesktop.Services;

/// <summary>
/// Per-session folder under Documents/LessRealBooth and bridge capture pull.
/// </summary>
public sealed class SessionWorkspace
{
    /// <summary>Bridge can wait up to ~20s internally for download; USB/save can add more.</summary>
    private static readonly HttpClient CaptureHttp = new() { Timeout = TimeSpan.FromSeconds(90) };

    private readonly string _sessionId;
    private readonly string _eventId;
    private readonly string _eventName;
    private readonly string _layoutId;
    private readonly object _manifestLock = new();
    private SessionManifest _manifest;
    private readonly string _eventRoot;

    public string SessionId => _sessionId;
    public string SessionRoot { get; }
    /// <summary>Documents/LessRealBooth/events/… folder containing <c>prints/</c>, <c>originals/</c>, <c>gallery_index.json</c>.</summary>
    public string EventRoot => _eventRoot;

    public SessionWorkspace(string eventId, string eventName, string layoutId)
    {
        _sessionId = Guid.NewGuid().ToString("N");
        _eventId = eventId;
        _eventName = eventName;
        _layoutId = layoutId;
        _eventRoot = LessRealBoothPaths.EventRootDirectory(_eventId, _eventName);
        SessionRoot = LessRealBoothPaths.SessionDirectory(_eventId, _eventName, _sessionId);
        var originals = LessRealBoothPaths.SessionOriginals(_eventId, _eventName, _sessionId);
        var final = LessRealBoothPaths.SessionFinal(_eventId, _eventName, _sessionId);
        Directory.CreateDirectory(originals);
        Directory.CreateDirectory(final);
        Directory.CreateDirectory(_eventRoot);
        Directory.CreateDirectory(LessRealBoothPaths.EventPrintsDirectory(_eventId, _eventName));
        Directory.CreateDirectory(LessRealBoothPaths.EventOriginalsDirectory(_eventId, _eventName));

        try
        {
            var readme = Path.Combine(SessionRoot, "README.txt");
            File.WriteAllText(readme,
                "LessRealBooth session folder\r\n" +
                "- originals\\    — one file per capture (from Sony bridge)\r\n" +
                "- final\\composite.png — template composite when layout pack is available\r\n" +
                "- session.json   — links captures + print + event-level paths for sharing station\r\n" +
                $"- ..\\prints\\ & ..\\originals\\ — same event: flat copies for client handoff\r\n" +
                $"- ..\\gallery_index.json — event-level index for preview / QR gallery\r\n");
        }
        catch
        {
            /* ignore */
        }

        _manifest = new SessionManifest
        {
            SessionId = _sessionId,
            EventId = _eventId,
            EventName = _eventName,
            LayoutId = _layoutId,
            CreatedUtc = DateTime.UtcNow,
            Captures = []
        };
        SaveManifest();
    }

    /// <summary>
    /// Calls sony_bridge GET /capture?nofocus=1 and copies the saved file into originals/shot_XXX.ext
    /// </summary>
    public async Task<CapturePullResult> TryPullCaptureFromBridgeAsync(int shotNumberOneBased,
        CancellationToken cancellationToken = default)
    {
        var originalsDir = LessRealBoothPaths.SessionOriginals(_eventId, _eventName, _sessionId);
        var url = $"http://127.0.0.1:18080/capture?nofocus=1&cb={DateTime.UtcNow.Ticks}";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RuntimeLog.Info("Capture", $"request shot={shotNumberOneBased} session={_sessionId} url=/capture?nofocus=1");
        try
        {
            using var response = await CaptureHttp.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                var hint = body.Length > 120 ? body[..120] + "…" : body;
                var err = $"invalid_json_http_{(int)response.StatusCode}";
                lock (_manifestLock)
                {
                    _manifest.Captures.Add(new CaptureEntry
                    {
                        Index = shotNumberOneBased,
                        Ok = false,
                        Error = err,
                        AddedUtc = DateTime.UtcNow
                    });
                    SaveManifest();
                }

                RuntimeLog.Warn("Capture", $"parse_fail shot={shotNumberOneBased} status={(int)response.StatusCode} body={hint}");
                return new CapturePullResult(false, null, err);
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
                {
                    var err = root.TryGetProperty("error", out var e) ? e.GetString() ?? "capture_failed" : "capture_failed";
                    lock (_manifestLock)
                    {
                        _manifest.Captures.Add(new CaptureEntry
                        {
                            Index = shotNumberOneBased,
                            Ok = false,
                            Error = err,
                            AddedUtc = DateTime.UtcNow
                        });
                        SaveManifest();
                    }
                    RuntimeLog.Warn("Capture", $"bridge_not_ok shot={shotNumberOneBased} err={err} ms={sw.ElapsedMilliseconds}");
                    return new CapturePullResult(false, null, err);
                }

                var srcPath = root.GetProperty("path").GetString();
                if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
                {
                    lock (_manifestLock)
                    {
                        _manifest.Captures.Add(new CaptureEntry
                        {
                            Index = shotNumberOneBased,
                            Ok = false,
                            Error = "path_missing",
                            AddedUtc = DateTime.UtcNow
                        });
                        SaveManifest();
                    }
                    RuntimeLog.Warn("Capture", $"path_missing shot={shotNumberOneBased} ms={sw.ElapsedMilliseconds}");
                    return new CapturePullResult(false, null, "path_missing");
                }

                var selectedSource = ResolveCompositorFriendlySource(srcPath!);
                if (!string.Equals(selectedSource, srcPath, StringComparison.OrdinalIgnoreCase))
                {
                    RuntimeLog.Info("Capture",
                        $"using sidecar for shot={shotNumberOneBased} src={Path.GetFileName(srcPath)} selected={Path.GetFileName(selectedSource)}");
                }
                else if (IsLikelyRawFile(srcPath!))
                {
                    RuntimeLog.Warn("Capture",
                        $"raw-only capture shot={shotNumberOneBased} file={Path.GetFileName(srcPath)}; composite may be blank unless camera saves JPEG sidecar");
                }

                var ext = Path.GetExtension(selectedSource);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
                var destName = $"shot_{shotNumberOneBased:000}{ext}";
                var destPath = Path.Combine(originalsDir, destName);
                File.Copy(selectedSource, destPath, overwrite: true);

                var eventOrigFileName = $"{_sessionId}_shot_{shotNumberOneBased:000}{ext}";
                var eventOrigRel = Path.Combine("originals", eventOrigFileName).Replace('\\', '/');
                try
                {
                    var eventOrigDir = LessRealBoothPaths.EventOriginalsDirectory(_eventId, _eventName);
                    Directory.CreateDirectory(eventOrigDir);
                    File.Copy(destPath, Path.Combine(eventOrigDir, eventOrigFileName), overwrite: true);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("Capture", $"event originals mirror failed session={_sessionId} err={ex.Message}");
                }

                lock (_manifestLock)
                {
                    var shotStem = $"{_sessionId}_shot_{shotNumberOneBased:000}";
                    _manifest.EventOriginalRelativePaths.RemoveAll(r =>
                        string.Equals(Path.GetFileNameWithoutExtension(r.Replace('/', Path.DirectorySeparatorChar)),
                            shotStem, StringComparison.OrdinalIgnoreCase));
                    _manifest.EventOriginalRelativePaths.Add(eventOrigRel);
                    _manifest.Captures.RemoveAll(c => c.Index == shotNumberOneBased);
                    _manifest.Captures.Add(new CaptureEntry
                    {
                        Index = shotNumberOneBased,
                        Ok = true,
                        RelativePath = Path.Combine("originals", destName).Replace('\\', '/'),
                        SourceBridgePath = srcPath,
                        AddedUtc = DateTime.UtcNow
                    });
                    SaveManifest();
                }

                RuntimeLog.Info("Capture", $"ok shot={shotNumberOneBased} ms={sw.ElapsedMilliseconds} file={destName}");
                return new CapturePullResult(true, destPath, null);
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                RuntimeLog.Warn("Capture", $"cancelled shot={shotNumberOneBased} ms={sw.ElapsedMilliseconds}");
                return new CapturePullResult(false, null, "cancelled");
            }

            lock (_manifestLock)
            {
                _manifest.Captures.Add(new CaptureEntry
                {
                    Index = shotNumberOneBased,
                    Ok = false,
                    Error = "capture_timeout",
                    AddedUtc = DateTime.UtcNow
                });
                SaveManifest();
            }
            RuntimeLog.Warn("Capture", $"timeout shot={shotNumberOneBased} ms={sw.ElapsedMilliseconds}");
            return new CapturePullResult(false, null, "capture_timeout");
        }
        catch (Exception ex)
        {
            lock (_manifestLock)
            {
                _manifest.Captures.Add(new CaptureEntry
                {
                    Index = shotNumberOneBased,
                    Ok = false,
                    Error = ex.Message,
                    AddedUtc = DateTime.UtcNow
                });
                SaveManifest();
            }
            RuntimeLog.Error("Capture", $"exception shot={shotNumberOneBased} ms={sw.ElapsedMilliseconds} msg={ex.Message}");
            return new CapturePullResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Builds final/composite.png from layout template.xml + session originals (center-crop guest photos into Photo slots).
    /// </summary>
    public (bool Ok, string? OutputPath, string? Error) TryComposeFinalPrint()
    {
        var packRoot = LayoutPackService.TryGetPackRoot(_layoutId);
        if (string.IsNullOrEmpty(packRoot))
            return (false, null, "layout_pack_not_found");

        var templatePath = LayoutPackService.FindTemplateXmlPath(packRoot);
        if (string.IsNullOrEmpty(templatePath))
            return (false, null, "template.xml_not_found");

        if (!DslrTemplateParser.TryParse(templatePath, out var parsed, out var parseErr) || parsed == null)
            return (false, null, parseErr);

        var shots = new Dictionary<int, string>();
        lock (_manifestLock)
        {
            foreach (var c in _manifest.Captures)
            {
                if (!c.Ok || string.IsNullOrEmpty(c.RelativePath)) continue;
                var full = Path.Combine(SessionRoot, c.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                    shots[c.Index] = full;
            }
        }

        var outAbs = Path.Combine(SessionRoot, "final", "composite.png");
        var preferVips = GlobalSettingsService.Load().PrintBehavior?.UseVipsCompositor ?? false;
        var (ok, engine, cerr) = Compositors.TryCompose(preferVips, parsed, packRoot, shots, outAbs);
        if (!ok) return (false, null, cerr);
        RuntimeLog.Info("Composite",
            $"engine={engine} session={_sessionId} output={outAbs}");

        var printRel = Path.Combine("prints", $"{_sessionId}.png").Replace('\\', '/');
        try
        {
            var printsDir = LessRealBoothPaths.EventPrintsDirectory(_eventId, _eventName);
            Directory.CreateDirectory(printsDir);
            File.Copy(outAbs, Path.Combine(printsDir, $"{_sessionId}.png"), overwrite: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Composite", $"event prints mirror failed session={_sessionId} err={ex.Message}");
        }

        lock (_manifestLock)
        {
            _manifest.FinalCompositeRelativePath = "final/composite.png";
            _manifest.EventPrintRelativePath = printRel;
            SaveManifest();
        }

        return (true, outAbs, null);
    }

    private void SaveManifest()
    {
        var path = LessRealBoothPaths.SessionManifestPath(_eventId, _eventName, _sessionId);
        var json = JsonSerializer.Serialize(_manifest,
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        File.WriteAllText(path, json);
        try
        {
            EventGalleryIndex.UpsertSession(_eventRoot, _manifest);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Session", $"gallery_index update failed session={_sessionId} err={ex.Message}");
        }
    }

    private static string ResolveCompositorFriendlySource(string srcPath)
    {
        if (!IsLikelyRawFile(srcPath))
            return srcPath;

        try
        {
            var dir = Path.GetDirectoryName(srcPath);
            var stem = Path.GetFileNameWithoutExtension(srcPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem))
                return srcPath;

            string[] preferredExt = [".jpg", ".jpeg", ".png"];
            foreach (var ext in preferredExt)
            {
                var sidecar = Path.Combine(dir, stem + ext);
                if (File.Exists(sidecar)) return sidecar;
                var sidecarUpper = Path.Combine(dir, stem + ext.ToUpperInvariant());
                if (File.Exists(sidecarUpper)) return sidecarUpper;
            }
        }
        catch
        {
            /* ignore and fall back to original */
        }

        return srcPath;
    }

    private static bool IsLikelyRawFile(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(ext)) return false;
        return ext.Equals(".arw", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".hif", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".raw", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".nef", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".cr2", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".cr3", StringComparison.OrdinalIgnoreCase);
    }
}

public readonly record struct CapturePullResult(bool Ok, string? LocalPath, string? Error);

public sealed class SessionManifest
{
    public required string SessionId { get; init; }
    public required string EventId { get; init; }
    public required string EventName { get; init; }
    public required string LayoutId { get; init; }
    public DateTime CreatedUtc { get; init; }
    public List<CaptureEntry> Captures { get; init; } = [];
    /// <summary>Relative to session root, e.g. final/composite.png</summary>
    public string? FinalCompositeRelativePath { get; set; }
    /// <summary>Relative to <b>event</b> root: flat print for handoff, e.g. prints/&lt;sessionId&gt;.png</summary>
    public string? EventPrintRelativePath { get; set; }
    /// <summary>Relative to <b>event</b> root: flat originals mirror, e.g. originals/&lt;sessionId&gt;_shot_001.jpg</summary>
    public List<string> EventOriginalRelativePaths { get; set; } = [];
    /// <summary>Opaque token for gallery URL (set when publishing / pairing QR).</summary>
    public string? ShareGalleryToken { get; set; }
    /// <summary>Base URL for the sharing station without trailing slash; QR encodes base + token.</summary>
    public string? ShareGalleryBaseUrl { get; set; }
    /// <summary>Number of print button actions used for this session (persisted).</summary>
    public int PrintActionsUsed { get; set; }
}

public sealed class CaptureEntry
{
    public int Index { get; init; }
    public bool Ok { get; init; }
    public string? RelativePath { get; init; }
    public string? SourceBridgePath { get; init; }
    public string? Error { get; init; }
    public DateTime AddedUtc { get; init; }
}
