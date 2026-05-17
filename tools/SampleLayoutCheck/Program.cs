using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BoothDesktop.Services;
using BoothDesktop.Services.Vips;

const string sampleRoot = @"D:\PHOTOBOOTH SOFTWARE\sample layout";
if (!Directory.Exists(sampleRoot))
{
    Console.Error.WriteLine($"Not found: {sampleRoot}");
    return 1;
}

var packArg = args.FirstOrDefault(a => a.StartsWith("--pack=", StringComparison.OrdinalIgnoreCase));
if (packArg != null)
{
    var packRootArg = packArg["--pack=".Length..].Trim('"');
    if (args.Any(a => a.Equals("--ab", StringComparison.OrdinalIgnoreCase)))
        return RunPackAb(packRootArg, args);
    return RunPackCompose(packRootArg, args);
}

var runCompose = args.Contains("--compose", StringComparer.OrdinalIgnoreCase);
if (runCompose)
{
    return RunComposeSmoke(sampleRoot);
}

foreach (var zip in Directory.GetFiles(sampleRoot, "*.zip").OrderBy(p => p))
{
    var name = Path.GetFileName(zip);
    var temp = Path.Combine(Path.GetTempPath(), "lrb_sample_" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(temp);
        ZipFile.ExtractToDirectory(zip, temp);
        var templatePath = Directory.GetFiles(temp, "template.xml", SearchOption.AllDirectories).FirstOrDefault();
        if (templatePath == null)
        {
            Console.WriteLine($"{name}: FAIL — no template.xml");
            continue;
        }

        if (!DslrTemplateParser.TryParse(templatePath, out var parsed, out var err) || parsed == null)
        {
            Console.WriteLine($"{name}: PARSE FAIL — {err}");
            continue;
        }

        var photoSlots = parsed.Layers.Count(l => l.IsPhotoSlot);
        var uniquePhotos = parsed.Layers.Where(l => l.IsPhotoSlot).Select(l => l.PhotoNumber).Distinct().Count();
        var staticImages = parsed.Layers.Count(l => !l.IsPhotoSlot);
        var preview = Directory.GetFiles(temp, "preview.png", SearchOption.AllDirectories).FirstOrDefault();

        Console.WriteLine($"{name}");
        Console.WriteLine($"  canvas: {parsed.CanvasWidth}x{parsed.CanvasHeight}");
        Console.WriteLine($"  resolutionDpi: {parsed.ResolutionDpi}");
        Console.WriteLine($"  printer slot: {parsed.PrinterSlot} (1=primary, 2=secondary)");
        Console.WriteLine($"  photo slots: {photoSlots} ({uniquePhotos} unique captures)");
        foreach (var layer in parsed.Layers.Where(l => l.IsPhotoSlot).OrderBy(l => l.PhotoNumber).ThenBy(l => l.Y))
        {
            Console.WriteLine(
                $"    photo {layer.PhotoNumber} slot at ({layer.X:F0},{layer.Y:F0}) {layer.W:F0}x{layer.H:F0}");
        }

        Console.WriteLine($"  static images: {staticImages}");
        Console.WriteLine($"  preview.png: {(preview != null ? "yes" : "MISSING")}");
        Console.WriteLine();
    }
    finally
    {
        try { Directory.Delete(temp, true); } catch { /* ignore */ }
    }
}

return 0;

static int RunPackCompose(string packRoot, string[] args)
{
    if (!LoadPack(packRoot, args, out var parsed, out var photoMap, out var outPng, out var err))
    {
        Console.Error.WriteLine(err);
        return 1;
    }

    var preferVips = args.Any(a => a.Equals("--vips", StringComparison.OrdinalIgnoreCase));
    var runs = ParseRuns(args);
    var engineLabel = preferVips ? "vips" : "wpf";

    Console.WriteLine($"Pack compose: {packRoot}");
    Console.WriteLine($"  engine={engineLabel} runs={runs}");
    Console.WriteLine($"  output={outPng}");
    foreach (var (n, p) in photoMap.OrderBy(kv => kv.Key))
        Console.WriteLine($"  photo {n} <- {Path.GetFileName(p)}");

    for (var i = 0; i < runs; i++)
    {
        var (ok, engineUsed, cerr) = Compositors.TryCompose(preferVips, parsed, packRoot, photoMap, outPng);
        if (!ok)
        {
            Console.Error.WriteLine(cerr);
            return 1;
        }
        if (i == runs - 1)
            Console.WriteLine($"  ok (engineUsed={engineUsed})");
    }

    return 0;
}

/// <summary>Runs WPF then Vips against the same inputs; reports composeMs deltas and pixel-diff stats.</summary>
static int RunPackAb(string packRoot, string[] args)
{
    if (!LoadPack(packRoot, args, out var parsed, out var photoMap, out var outBase, out var err))
    {
        Console.Error.WriteLine(err);
        return 1;
    }

    var runs = ParseRuns(args);
    var dir = Path.GetDirectoryName(outBase) ?? packRoot;
    var stem = Path.GetFileNameWithoutExtension(outBase);
    var wpfPath = Path.Combine(dir, $"{stem}.wpf.png");
    var vipsPath = Path.Combine(dir, $"{stem}.vips.png");

    Console.WriteLine($"Pack A/B compose: {packRoot}");
    Console.WriteLine($"  runs={runs} (last result kept on disk)");
    Console.WriteLine($"  wpf  -> {wpfPath}");
    Console.WriteLine($"  vips -> {vipsPath}");
    foreach (var (n, p) in photoMap.OrderBy(kv => kv.Key))
        Console.WriteLine($"  photo {n} <- {Path.GetFileName(p)}");

    Console.WriteLine("");
    Console.WriteLine("--- WPF compositor ---");
    if (!RunNTimes(false, runs, parsed, packRoot, photoMap, wpfPath))
        return 1;

    Console.WriteLine("");
    Console.WriteLine("--- Vips compositor ---");
    if (!VipsTemplateCompositor.IsAvailable)
    {
        Console.Error.WriteLine("libvips probe failed — cannot run A/B (see runtime log for details).");
        return 2;
    }
    if (!RunNTimes(true, runs, parsed, packRoot, photoMap, vipsPath))
        return 1;

    Console.WriteLine("");
    Console.WriteLine("--- pixel diff (WPF vs Vips) ---");
    PrintPixelDiff(wpfPath, vipsPath);
    Console.WriteLine("");
    Console.WriteLine("--- file sizes ---");
    Console.WriteLine($"  wpf  bytes={new FileInfo(wpfPath).Length}");
    Console.WriteLine($"  vips bytes={new FileInfo(vipsPath).Length}");
    return 0;
}

static bool RunNTimes(bool preferVips, int runs, ParsedTemplate parsed, string packRoot,
    IReadOnlyDictionary<int, string> photoMap, string outPath)
{
    for (var i = 0; i < runs; i++)
    {
        var (ok, engineUsed, cerr) = Compositors.TryCompose(preferVips, parsed, packRoot, photoMap, outPath);
        if (!ok)
        {
            Console.Error.WriteLine($"  run {i + 1}/{runs} FAILED: {cerr}");
            return false;
        }
        if (i == runs - 1)
            Console.WriteLine($"  run {i + 1}/{runs} ok engineUsed={engineUsed} (see [CompositeQuality] in runtime log)");
    }
    return true;
}

static void PrintPixelDiff(string aPath, string bPath)
{
    try
    {
        using var aStream = File.OpenRead(aPath);
        using var bStream = File.OpenRead(bPath);
        var aDec = BitmapDecoder.Create(aStream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var bDec = BitmapDecoder.Create(bStream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var aFrame = aDec.Frames[0];
        var bFrame = bDec.Frames[0];
        if (aFrame.PixelWidth != bFrame.PixelWidth || aFrame.PixelHeight != bFrame.PixelHeight)
        {
            Console.WriteLine($"  size mismatch wpf={aFrame.PixelWidth}x{aFrame.PixelHeight} vips={bFrame.PixelWidth}x{bFrame.PixelHeight}");
            return;
        }
        var w = aFrame.PixelWidth;
        var h = aFrame.PixelHeight;
        var stride = w * 4;
        var aBytes = new byte[stride * h];
        var bBytes = new byte[stride * h];
        var aConv = new FormatConvertedBitmap(aFrame, PixelFormats.Pbgra32, null, 0);
        var bConv = new FormatConvertedBitmap(bFrame, PixelFormats.Pbgra32, null, 0);
        aConv.CopyPixels(aBytes, stride, 0);
        bConv.CopyPixels(bBytes, stride, 0);

        long sumAbs = 0;
        long sumSq = 0;
        int maxAbs = 0;
        long count = 0;
        for (var i = 0; i < aBytes.Length; i++)
        {
            // Skip alpha byte to match a typical PSNR metric on RGB.
            if ((i % 4) == 3) continue;
            int d = aBytes[i] - bBytes[i];
            int ad = d < 0 ? -d : d;
            sumAbs += ad;
            sumSq += (long)d * d;
            if (ad > maxAbs) maxAbs = ad;
            count++;
        }

        var mae = (double)sumAbs / count;
        var mse = (double)sumSq / count;
        // PSNR in dB; clamp when images are identical to avoid divide-by-zero.
        var psnrDb = mse > 0 ? 10.0 * Math.Log10(255.0 * 255.0 / mse) : double.PositiveInfinity;

        Console.WriteLine($"  pixels(RGB)={count}");
        Console.WriteLine($"  mae={mae:F3}  rmse={Math.Sqrt(mse):F3}  maxAbsDiff={maxAbs}");
        Console.WriteLine($"  psnr={psnrDb:F2}dB (higher = more similar; >30 = near-identical, >40 = effectively pixel-equal)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  pixel diff failed: {ex.Message}");
    }
}

static bool LoadPack(string packRoot, string[] args,
    out ParsedTemplate parsed, out Dictionary<int, string> photoMap,
    out string outPng, out string err)
{
    parsed = null!;
    photoMap = new();
    outPng = "";
    err = "";

    var templatePath = Path.Combine(packRoot, "template.xml");
    var outArg = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.OrdinalIgnoreCase));
    outPng = outArg != null
        ? outArg["--out=".Length..].Trim('"')
        : Path.Combine(packRoot, "_debug_compose.png");
    var shotsDir = args.FirstOrDefault(a => a.StartsWith("--shots=", StringComparison.OrdinalIgnoreCase));
    shotsDir = shotsDir != null ? shotsDir["--shots=".Length..].Trim('"') : packRoot;

    if (!DslrTemplateParser.TryParse(templatePath, out var p, out var perr) || p == null)
    {
        err = perr;
        return false;
    }
    parsed = p;

    foreach (var f in Directory.GetFiles(shotsDir, "*.*", SearchOption.TopDirectoryOnly))
    {
        var ext = Path.GetExtension(f);
        if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            continue;

        var name = Path.GetFileNameWithoutExtension(f);
        int n;
        if (name.StartsWith("shot_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(5), out n))
        {
            /* session originals: shot_001 */
        }
        else
        {
            var idx = name.LastIndexOf("_shot_", StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || !int.TryParse(name.AsSpan(idx + 6), out n)) continue;
        }

        if (n <= 0) continue;
        photoMap[n] = f;
    }

    return true;
}

static int ParseRuns(string[] args)
{
    var arg = args.FirstOrDefault(a => a.StartsWith("--runs=", StringComparison.OrdinalIgnoreCase));
    if (arg == null) return 1;
    if (int.TryParse(arg["--runs=".Length..], out var n) && n > 0 && n < 100) return n;
    return 1;
}

static int RunComposeSmoke(string sampleRoot)
{
    var zipPath = Directory.GetFiles(sampleRoot, "*STRIP*.zip", SearchOption.TopDirectoryOnly)
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();
    if (zipPath == null)
    {
        Console.Error.WriteLine("No *STRIP*.zip in sample folder.");
        return 1;
    }

    var shots = Directory.GetFiles(sampleRoot, "*_shot_*.*", SearchOption.TopDirectoryOnly)
        .Where(p => p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        .Take(3)
        .ToList();
    if (shots.Count < 3)
    {
        Console.Error.WriteLine("Need at least 3 *_shot_*.JPG files in sample folder.");
        return 1;
    }

    var temp = Path.Combine(Path.GetTempPath(), "lrb_compose_" + Guid.NewGuid().ToString("N"));
    var outDir = Path.Combine(sampleRoot, "_compose_smoke_out");
    Directory.CreateDirectory(outDir);
    var outPng = Path.Combine(outDir, "composite_smoke.png");

    try
    {
        Directory.CreateDirectory(temp);
        ZipFile.ExtractToDirectory(zipPath, temp);
        var templatePath = Directory.GetFiles(temp, "template.xml", SearchOption.AllDirectories).First();
        if (!DslrTemplateParser.TryParse(templatePath, out var parsed, out var err) || parsed == null)
        {
            Console.Error.WriteLine(err);
            return 1;
        }

        var packRoot = Path.GetDirectoryName(templatePath)!;
        var photoMap = new Dictionary<int, string>();
        for (var i = 0; i < shots.Count; i++)
            photoMap[i + 1] = shots[i];

        Console.WriteLine($"Compose smoke: {Path.GetFileName(zipPath)}");
        Console.WriteLine($"  packRoot={packRoot}");
        Console.WriteLine($"  output={outPng}");
        foreach (var (n, p) in photoMap)
            Console.WriteLine($"  photo {n} <- {Path.GetFileName(p)}");

        if (!TemplateCompositor.TryComposeToPng(parsed, packRoot, photoMap, outPng, out var cerr))
        {
            Console.Error.WriteLine($"Compose failed: {cerr}");
            return 1;
        }

        using (var stream = File.OpenRead(outPng))
        {
            var dec = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = dec.Frames[0];
            Console.WriteLine($"  savedPx={frame.PixelWidth}x{frame.PixelHeight} embeddedDpi={frame.DpiX:F0}x{frame.DpiY:F0}");
        }

        Console.WriteLine($"  fileBytes={new FileInfo(outPng).Length}");
        var logDir = LessRealBoothPaths.LogsRoot;
        Console.WriteLine($"  see runtime log under: {logDir}");
        Console.WriteLine("  search for [CompositeQuality] in the newest runtime_*.log");
        return 0;
    }
    finally
    {
        try { Directory.Delete(temp, true); } catch { /* ignore */ }
    }
}
