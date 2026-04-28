using BoothDesktop.Models;

namespace BoothDesktop.Services;

/// <summary>Built-in layout ids (Assets/Layouts/lay_*) used by picker and event allowlists.</summary>
public static class BuiltInLayouts
{
    public static IReadOnlyList<BoothLayoutOption> All()
    {
        var list = new List<BoothLayoutOption>
        {
            new()
            {
                Id = "lay_af1",
                DisplayName = "Cleo & Heart — 3 photos (mirrored)",
                ShotCount = 3,
                PreviewKey = "af1"
            },
            new()
            {
                Id = "lay_h2",
                DisplayName = "Cleo & Heart 2 — 4 photos",
                ShotCount = 4,
                PreviewKey = "h2"
            },
            new()
            {
                Id = "lay_h3",
                DisplayName = "Cleo & Heart 3 — landscape 2",
                ShotCount = 2,
                PreviewKey = "h3"
            }
        };
        foreach (var lo in list)
            lo.ResolvedPreviewPath = LayoutPreviewResolver.Resolve(lo.Id);
        return list;
    }
}
