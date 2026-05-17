using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using BoothDesktop.Models;

namespace BoothDesktop.Services;

public sealed class BoothPrintResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public int PrinterSlot { get; init; } = 1;
    public string? ResolvedPrinterName { get; init; }
    public int Copies { get; init; } = 1;
}

/// <summary>Sends composite PNG to a Windows print queue using driver defaults (paper, quality, etc.).</summary>
public static class BoothPrintService
{
    public static (bool Ok, string Message) TryPrintForLayout(string imagePath, string layoutId, int? copiesPerJob = null)
    {
        var primarySlot = TemplatePrintRouting.ResolveSlotForLayout(layoutId);
        var copies = copiesPerJob ?? SessionPrintService.ResolveCopiesForJob(primarySlot);
        var result = TryPrint(imagePath, primarySlot, copies, layoutId);
        if (!result.Ok)
            return (false, result.Message);

        var messages = new List<string> { result.Message };
        var behavior = GlobalSettingsService.Load().PrintBehavior;
        if (behavior.PrintToBothPrinters)
        {
            var otherSlot = primarySlot == 1 ? 2 : 1;
            var otherCopies = copiesPerJob ?? SessionPrintService.ResolveCopiesForJob(otherSlot);
            var second = TryPrint(imagePath, otherSlot, otherCopies, layoutId);
            if (!second.Ok)
                return (false, $"{result.Message}\n\nSecond printer failed: {second.Message}");
            messages.Add(second.Message);
        }

        return (true, string.Join(Environment.NewLine, messages));
    }

    public static BoothPrintResult TryPrint(string imagePath, int printerSlot, int? copiesOverride = null,
        string? layoutId = null, GlobalAppSettings? settingsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return Fail(1, "Print file not found.");

        using var image = Image.FromFile(imagePath);
        return TryPrintImage(image, printerSlot, copiesOverride, layoutId, settingsOverride);
    }

    public static BoothPrintResult TryPrintAlignmentTestPage(int printerSlot, GlobalAppSettings? settingsOverride = null)
    {
        using var image = BoothPrintTestImage.CreatePortrait4x6($"Printer {printerSlot} — alignment test");
        return TryPrintImage(image, printerSlot, 1, layoutId: null, settingsOverride);
    }

    private static BoothPrintResult TryPrintImage(Image image, int printerSlot, int? copiesOverride,
        string? layoutId, GlobalAppSettings? settingsOverride)
    {
        var slot = printerSlot == 2 ? 2 : 1;
        var global = settingsOverride ?? GlobalSettingsService.Load();
        var slotSettings = slot == 2 ? global.Printer2 : global.Printer1;
        var alignment = PrinterAlignmentResolver.Resolve(slot, global);
        var copies = Math.Clamp(copiesOverride ?? slotSettings.Copies, 1, 99);
        var queueName = string.IsNullOrWhiteSpace(slotSettings.PrinterName)
            ? null
            : slotSettings.PrinterName.Trim();

        if (!string.IsNullOrEmpty(queueName) && !IsPrinterInstalled(queueName))
        {
            return Fail(slot,
                $"Printer {slot} is set to “{queueName}” but that queue is not installed on this PC. Open Global settings and pick an available printer.");
        }

        try
        {
            using var doc = new PrintDocument();
            if (!string.IsNullOrEmpty(queueName))
                doc.PrinterSettings.PrinterName = queueName;

            var devMode = slotSettings.GetDriverPreferencesBytes();
            if (devMode != null)
                PrinterDriverPreferencesService.ApplyTo(doc.PrinterSettings, devMode);

            doc.PrinterSettings.Copies = (short)copies;
            doc.PrintController = new StandardPrintController();
            doc.OriginAtMargins = false;
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

            doc.PrintPage += (_, e) =>
            {
                BoothPrintLayout.DrawImageOnPage(e.Graphics!, image, e, layoutId, alignment);
                e.HasMorePages = false;
            };

            doc.Print();

            var usedName = string.IsNullOrEmpty(doc.PrinterSettings.PrinterName)
                ? "(Windows default)"
                : doc.PrinterSettings.PrinterName;

            RuntimeLog.Info("Print",
                $"ok slot={slot} queue={usedName} copies={copies} " +
                $"align={alignment.ScalePercent}% off=({alignment.OffsetXHundredths},{alignment.OffsetYHundredths}) " +
                $"followP1={alignment.FollowedPrinter1}");

            var modeHint = string.IsNullOrWhiteSpace(slotSettings.ProfileLabel)
                ? ""
                : $" — {slotSettings.ProfileLabel}";

            return new BoothPrintResult
            {
                Ok = true,
                Message = copies == 1
                    ? $"Sent to printer {slot} ({usedName}){modeHint}."
                    : $"Sent {copies} copies to printer {slot} ({usedName}){modeHint}.",
                PrinterSlot = slot,
                ResolvedPrinterName = usedName,
                Copies = copies
            };
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Print", $"failed slot={slot} err={ex.Message}");
            return Fail(slot, ex.Message);
        }
    }

    private static BoothPrintResult Fail(int slot, string message) =>
        new() { Ok = false, Message = message, PrinterSlot = slot };

    private static bool IsPrinterInstalled(string printerName)
    {
        foreach (string name in PrinterSettings.InstalledPrinters)
        {
            if (string.Equals(name, printerName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
