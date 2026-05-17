using BoothDesktop.Services.Vips;

namespace BoothDesktop.Services;

/// <summary>
/// Facade that routes composite requests to either the WPF compositor (default, proven) or the
/// libvips compositor (Phase 2, opt-in via <see cref="Models.PrintBehaviorSettings.UseVipsCompositor"/>).
///
/// When the Vips path is requested but libvips fails to load or a render throws, we automatically
/// fall back to the WPF compositor and emit a warning so the guest never sees a hard failure.
/// </summary>
public static class Compositors
{
    public enum CompositorKind
    {
        Wpf,
        Vips
    }

    /// <summary>Render a composite, picking the engine per the feature flag with graceful fallback.</summary>
    /// <param name="preferVips">When true, try Vips first; fall back to WPF on probe failure or render error.</param>
    /// <returns>(ok, engineActuallyUsed, error). On fallback, engineActuallyUsed is Wpf.</returns>
    public static (bool Ok, CompositorKind Engine, string? Error) TryCompose(
        bool preferVips,
        ParsedTemplate template, string packRoot,
        IReadOnlyDictionary<int, string> photoNumberToOriginalPath, string outputPngPath)
    {
        if (preferVips)
        {
            if (!VipsTemplateCompositor.IsAvailable)
            {
                RuntimeLog.Warn("Compositors",
                    "UseVipsCompositor=true but libvips is unavailable on this machine; falling back to WPF compositor.");
            }
            else if (VipsTemplateCompositor.TryComposeToPng(template, packRoot,
                         photoNumberToOriginalPath, outputPngPath, out var vipsErr))
            {
                return (true, CompositorKind.Vips, null);
            }
            else
            {
                RuntimeLog.Warn("Compositors",
                    $"Vips compose failed ({vipsErr}); falling back to WPF compositor for output={outputPngPath}");
            }
        }

        if (TemplateCompositor.TryComposeToPng(template, packRoot,
                photoNumberToOriginalPath, outputPngPath, out var wpfErr))
        {
            return (true, CompositorKind.Wpf, null);
        }

        return (false, CompositorKind.Wpf, wpfErr);
    }
}
