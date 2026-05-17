using System.Xml;

namespace BoothDesktop.Services;

/// <summary>
/// Reads which virtual booth printer (1 or 2) a layout uses from dslrBooth/LumaBooth template.xml.
/// LumaBooth exposes “print to secondary printer” in layout settings; exports may encode that on the root or a Printing node.
/// </summary>
public static class TemplatePrintRouting
{
    public const int MinSlot = 1;
    public const int MaxSlot = 2;

    public static int ResolveSlotForLayout(string layoutId)
    {
        if (string.IsNullOrWhiteSpace(layoutId)) return 1;
        if (!LayoutSlotGuideService.TryLoadTemplateForLayout(layoutId, out var parsed, out _) || parsed == null)
            return 1;
        return parsed.PrinterSlot;
    }

    public static int ReadPrinterSlot(XmlElement root)
    {
        if (TryReadSlotFromElement(root, out var slot))
            return slot;

        foreach (XmlNode node in root.ChildNodes)
        {
            if (node is not XmlElement el) continue;
            if (!IsPrinterSettingsElement(el.LocalName)) continue;
            if (TryReadSlotFromElement(el, out slot))
                return slot;
        }

        return 1;
    }

    private static bool IsPrinterSettingsElement(string localName) =>
        localName.Equals("Printing", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("Printer", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("PrinterSettings", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("PrintSettings", StringComparison.OrdinalIgnoreCase)
        || localName.Equals("PrintOptions", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadSlotFromElement(XmlElement el, out int slot)
    {
        slot = 1;

        if (TryParseSecondaryFlag(el, out var isSecondary) && isSecondary)
        {
            slot = 2;
            return true;
        }

        if (TryParseExplicitSlot(el, out slot))
            return true;

        return false;
    }

    private static bool TryParseSecondaryFlag(XmlElement el, out bool isSecondary)
    {
        isSecondary = false;
        foreach (var name in new[]
                 {
                     "PrintToSecondaryPrinter", "SecondaryPrinter", "UseSecondaryPrinter", "PrintToSecondary",
                     "Secondary", "IsSecondaryPrinter", "UseSecondary", "SecondPrinter"
                 })
        {
            var v = GetAttr(el, name);
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (IsTruthy(v))
            {
                isSecondary = true;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseExplicitSlot(XmlElement el, out int slot)
    {
        slot = 1;
        foreach (var name in new[]
                 {
                     "PrintTo", "Printer", "PrinterNumber", "PrinterNum", "PrinterId",
                     "PrintPrinter", "AssignedPrinter", "TargetPrinter", "BoothPrinter"
                 })
        {
            var v = GetAttr(el, name);
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (TryParseSlotValue(v, out slot))
                return true;
        }

        return false;
    }

    private static bool TryParseSlotValue(string raw, out int slot)
    {
        slot = 1;
        raw = raw.Trim();
        if (raw.Length == 0) return false;

        if (int.TryParse(raw, out var n))
        {
            slot = n <= 1 ? 1 : 2;
            return true;
        }

        if (raw.Equals("secondary", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("second", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("printer2", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("printer 2", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("2nd", StringComparison.OrdinalIgnoreCase))
        {
            slot = 2;
            return true;
        }

        if (raw.Equals("primary", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("first", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("printer1", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("printer 1", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("1st", StringComparison.OrdinalIgnoreCase))
        {
            slot = 1;
            return true;
        }

        return false;
    }

    private static bool IsTruthy(string v) =>
        v.Equals("1", StringComparison.OrdinalIgnoreCase)
        || v.Equals("true", StringComparison.OrdinalIgnoreCase)
        || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || v.Equals("on", StringComparison.OrdinalIgnoreCase);

    private static string? GetAttr(XmlElement el, string name)
    {
        if (el.HasAttribute(name)) return el.GetAttribute(name);
        var found = el.Attributes?.Cast<XmlAttribute>()
            .FirstOrDefault(a => string.Equals(a.LocalName, name, StringComparison.OrdinalIgnoreCase));
        return found?.Value;
    }
}
