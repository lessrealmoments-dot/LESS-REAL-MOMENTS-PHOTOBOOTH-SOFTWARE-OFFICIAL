using BoothDesktop.Models;

namespace BoothDesktop.Services;

/// <summary>Effective alignment for a print job (per slot, optional follow printer 1).</summary>
public readonly struct EffectivePrinterAlignment
{
    public int ScalePercent { get; init; }
    /// <summary>Horizontal nudge in 1/100 inch (positive = right).</summary>
    public int OffsetXHundredths { get; init; }
    /// <summary>Vertical nudge in 1/100 inch (positive = down).</summary>
    public int OffsetYHundredths { get; init; }
    public bool FollowedPrinter1 { get; init; }
}

public static class PrinterAlignmentResolver
{
    public static EffectivePrinterAlignment Resolve(int printerSlot, GlobalAppSettings global)
    {
        var slot = printerSlot == 2 ? global.Printer2 : global.Printer1;
        if (printerSlot == 2 && slot.FollowPrinter1Alignment)
        {
            var effective = new EffectivePrinterAlignment
            {
                ScalePercent = global.Printer1.AlignmentScalePercent,
                OffsetXHundredths = global.Printer1.AlignmentOffsetXHundredths,
                OffsetYHundredths = global.Printer1.AlignmentOffsetYHundredths,
                FollowedPrinter1 = true
            };
            return effective;
        }

        var own = new EffectivePrinterAlignment
        {
            ScalePercent = slot.AlignmentScalePercent,
            OffsetXHundredths = slot.AlignmentOffsetXHundredths,
            OffsetYHundredths = slot.AlignmentOffsetYHundredths,
            FollowedPrinter1 = false
        };

        return own;
    }

    public static void NormalizeSlot(PrinterSlotSettings slot)
    {
        slot.AlignmentScalePercent = Math.Clamp(slot.AlignmentScalePercent, 50, 150);
        slot.AlignmentOffsetXHundredths = Math.Clamp(slot.AlignmentOffsetXHundredths, -500, 500);
        slot.AlignmentOffsetYHundredths = Math.Clamp(slot.AlignmentOffsetYHundredths, -500, 500);
    }
}
