using System.Windows;

namespace BoothDesktop.Services;

/// <summary>Loads template.xml slot geometry for live-view positioning guides.</summary>
public static class LayoutSlotGuideService
{
    public static bool TryLoadTemplateForLayout(string layoutId, out ParsedTemplate? parsed, out string error)
    {
        parsed = null;
        error = "";

        if (string.IsNullOrWhiteSpace(layoutId))
        {
            error = "layout_id_empty";
            return false;
        }

        var packRoot = LayoutPackService.TryGetPackRoot(layoutId);
        if (string.IsNullOrEmpty(packRoot))
        {
            error = "layout_pack_not_found";
            return false;
        }

        var templatePath = LayoutPackService.FindTemplateXmlPath(packRoot);
        if (string.IsNullOrEmpty(templatePath))
        {
            error = "template.xml_not_found";
            return false;
        }

        if (!DslrTemplateParser.TryParse(templatePath, out parsed, out error) || parsed == null)
            return false;

        return true;
    }

    /// <summary>
    /// Slot W×H for the active shot (live guide uses aspect ratio only, centered on preview).
    /// dslrBooth often repeats the same PhotoNumber on multiple strip positions — one framing window per capture.
    /// </summary>
    public static bool TryGetLiveGuideSlotForPhoto(ParsedTemplate template, int photoNumberOneBased,
        out Rect slotRect, out int duplicateSlotCount)
    {
        slotRect = default;
        duplicateSlotCount = 0;

        if (photoNumberOneBased <= 0)
            return false;

        var matches = template.Layers
            .Where(l => l.IsPhotoSlot && l.PhotoNumber == photoNumberOneBased)
            .OrderBy(l => l.Sequence)
            .ToList();

        if (matches.Count == 0)
            return false;

        duplicateSlotCount = matches.Count;

        // Prefer the largest slot if sizes differ; tie-break by document order.
        var primary = matches
            .OrderByDescending(l => l.W * l.H)
            .ThenBy(l => l.Sequence)
            .First();

        slotRect = new Rect(primary.X, primary.Y, primary.W, primary.H);
        return true;
    }
}
