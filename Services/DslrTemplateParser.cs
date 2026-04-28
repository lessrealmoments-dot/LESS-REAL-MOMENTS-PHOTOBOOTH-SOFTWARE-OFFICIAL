using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace BoothDesktop.Services;

public sealed class TemplateLayer
{
    /// <summary>Document order for tie-breaking when Z matches.</summary>
    public int Sequence { get; init; }
    public required int Z { get; init; }
    public bool IsPhotoSlot { get; init; }
    /// <summary>1-based photo number when <see cref="IsPhotoSlot"/>.</summary>
    public int PhotoNumber { get; init; }
    /// <summary>Relative path from pack root for static bitmaps.</summary>
    public string? RelativeImagePath { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
}

public sealed class ParsedTemplate
{
    public required int CanvasWidth { get; init; }
    public required int CanvasHeight { get; init; }
    public required string TemplateFilePath { get; init; }
    public required IReadOnlyList<TemplateLayer> Layers { get; init; }
}

/// <summary>Parses dslrBooth-style template.xml: canvas size, &lt;Photo&gt; slots, static &lt;Image&gt; elements (paths from ImagePath etc.), ZIndex order.</summary>
public static class DslrTemplateParser
{
    public static bool TryParse(string templateXmlPath, out ParsedTemplate? parsed, out string error)
    {
        parsed = null;
        error = "";
        if (string.IsNullOrWhiteSpace(templateXmlPath) || !File.Exists(templateXmlPath))
        {
            error = "template.xml not found.";
            return false;
        }

        try
        {
            var doc = new XmlDocument();
            doc.Load(templateXmlPath);
            var root = doc.DocumentElement;
            if (root == null)
            {
                error = "Empty template.";
                return false;
            }

            var packRoot = Path.GetDirectoryName(templateXmlPath)!;
            var layers = new List<TemplateLayer>();
            var z = 0;
            var seq = 0;
            WalkElements(root, packRoot, layers, ref z, ref seq);

            var (cw, ch) = ReadCanvasSize(root);
            if ((cw <= 0 || ch <= 0) && layers.Count > 0)
            {
                cw = Math.Max(1, (int)Math.Ceiling(layers.Max(l => l.X + l.W)));
                ch = Math.Max(1, (int)Math.Ceiling(layers.Max(l => l.Y + l.H)));
            }

            if (cw <= 0 || ch <= 0)
            {
                error = $"Invalid or missing canvas size (got {cw}x{ch}). Add Width/Height on template root or a Size element.";
                return false;
            }

            parsed = new ParsedTemplate
            {
                CanvasWidth = cw,
                CanvasHeight = ch,
                TemplateFilePath = templateXmlPath,
                Layers = layers
            };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void WalkElements(XmlNode node, string packRoot, List<TemplateLayer> layers, ref int z, ref int seq)
    {
        if (node is XmlElement el)
        {
            if (TryCreateLayer(el, packRoot, z, seq, out var layer))
            {
                layers.Add(layer);
                z++;
                seq++;
            }
        }

        foreach (XmlNode child in node.ChildNodes)
            WalkElements(child, packRoot, layers, ref z, ref seq);
    }

    private static bool TryCreateLayer(XmlElement el, string packRoot, int z, int sequence, out TemplateLayer layer)
    {
        var name = el.LocalName;
        if (string.Equals(name, "Photo", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetInt(el, out var photoNum, "PhotoNumber", "PhotoNum", "Index", "Number"))
            {
                layer = null!;
                return false;
            }

            if (!TryGetRect(el, out var x, out var y, out var w, out var h))
            {
                layer = null!;
                return false;
            }

            layer = new TemplateLayer
            {
                Sequence = sequence,
                Z = ReadZ(el, z),
                IsPhotoSlot = true,
                PhotoNumber = photoNum,
                RelativeImagePath = null,
                X = x,
                Y = y,
                W = w,
                H = h
            };
            return true;
        }

        if (IsStaticImageElement(name))
        {
            var rel = GetImageRelativePath(el, packRoot);
            if (string.IsNullOrEmpty(rel))
            {
                layer = null!;
                return false;
            }

            if (!TryGetRect(el, out var x, out var y, out var w, out var h))
            {
                layer = null!;
                return false;
            }

            layer = new TemplateLayer
            {
                Sequence = sequence,
                Z = ReadZ(el, z),
                IsPhotoSlot = false,
                PhotoNumber = 0,
                RelativeImagePath = rel,
                X = x,
                Y = y,
                W = w,
                H = h
            };
            return true;
        }

        layer = null!;
        return false;
    }

    private static bool IsStaticImageElement(string localName) =>
        localName.Equals("Image", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("Bitmap", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("Picture", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("Graphic", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("StaticImage", StringComparison.OrdinalIgnoreCase);

    private static int ReadZ(XmlElement el, int fallback)
    {
        if (TryGetDouble(el, out var z, "ZIndex", "Z", "Layer", "Order")) return (int)Math.Round(z);
        return fallback;
    }

    private static string? GetImageRelativePath(XmlElement el, string packRoot)
    {
        var raw = GetAttr(el, "ImagePath", "File", "Source", "Src", "Path", "Image", "Bitmap", "Filename", "Name");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (raw.Contains("..", StringComparison.Ordinal)) return null;

        var abs = Path.GetFullPath(Path.Combine(packRoot, raw));
        var rootFull = Path.GetFullPath(packRoot);
        if (!abs.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;

        return Path.GetRelativePath(packRoot, abs);
    }

    private static (int w, int h) ReadCanvasSize(XmlElement root)
    {
        if (TryGetInt(root, out var w, "Width", "CanvasWidth", "PrintWidth", "PageWidth")
            && TryGetInt(root, out var h, "Height", "CanvasHeight", "PrintHeight", "PageHeight"))
            return (w, h);

        foreach (XmlNode n in root.ChildNodes)
        {
            if (n is not XmlElement c) continue;
            if (!c.LocalName.Equals("Size", StringComparison.OrdinalIgnoreCase)
                && !c.LocalName.Equals("Canvas", StringComparison.OrdinalIgnoreCase)
                && !c.LocalName.Equals("PrintArea", StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryGetInt(c, out w, "Width", "W") && TryGetInt(c, out h, "Height", "H"))
                return (w, h);
        }

        return (0, 0);
    }

    private static bool TryGetRect(XmlElement el, out double x, out double y, out double w, out double h)
    {
        x = y = w = h = 0;
        if (!TryGetDouble(el, out x, "X", "Left", "Canvas.Left", "LeftPx")) return false;
        if (!TryGetDouble(el, out y, "Y", "Top", "Canvas.Top", "TopPx")) return false;
        if (!TryGetDouble(el, out w, "Width", "W", "ActualWidth")) return false;
        if (!TryGetDouble(el, out h, "Height", "H", "ActualHeight")) return false;
        if (w <= 0 || h <= 0) return false;
        return true;
    }

    private static bool TryGetInt(XmlElement el, out int v, params string[] attrNames)
    {
        v = 0;
        if (!TryGetDouble(el, out var d, attrNames)) return false;
        v = (int)Math.Round(d);
        return true;
    }

    private static bool TryGetDouble(XmlElement el, out double v, params string[] attrNames)
    {
        v = 0;
        var s = GetAttr(el, attrNames);
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
               || double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v);
    }

    private static string? GetAttr(XmlElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.HasAttribute(n)) return el.GetAttribute(n);
            var found = el.Attributes?.Cast<XmlAttribute>()
                .FirstOrDefault(a => string.Equals(a.LocalName, n, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found.Value;
        }

        return null;
    }
}
