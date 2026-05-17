using System.ComponentModel;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BoothDesktop.Services;

/// <summary>
/// Opens the native Windows printer driver Preferences dialog and stores DEVMODE per virtual slot
/// (same physical queue can have different saved modes — e.g. HiTi 4×6 vs 2-up strip).
/// </summary>
public static class PrinterDriverPreferencesService
{
    private const int DM_OUT_BUFFER = 0x00000002;
    private const int DM_IN_BUFFER = 0x00000008;
    private const int DM_IN_PROMPT = 0x00000004;

    public enum ConfigureResult
    {
        Ok,
        Cancelled,
        NoPrinter,
        Failed
    }

    public static ConfigureResult TryConfigure(Window? owner, string printerName,
        byte[]? existingDevMode, out byte[]? updatedDevMode, out string message)
    {
        updatedDevMode = null;
        message = "";

        if (string.IsNullOrWhiteSpace(printerName))
        {
            message = "Select a printer queue first.";
            return ConfigureResult.NoPrinter;
        }

        if (!IsPrinterInstalled(printerName))
        {
            message = $"Printer “{printerName}” is not installed.";
            return ConfigureResult.NoPrinter;
        }

        var hwnd = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;

        try
        {
            if (!TryShowDocumentProperties(hwnd, printerName, existingDevMode, out updatedDevMode))
            {
                message = "Printer preferences were not saved.";
                return ConfigureResult.Cancelled;
            }

            message = "Driver preferences saved for this slot.";
            return ConfigureResult.Ok;
        }
        catch (Win32Exception ex)
        {
            RuntimeLog.Warn("Print", $"DocumentProperties failed printer={printerName} err={ex.Message}");
            message = ex.Message;
            return ConfigureResult.Failed;
        }
    }

    public static void ApplyTo(PrinterSettings settings, byte[]? devModeBytes)
    {
        if (devModeBytes is not { Length: > 0 }) return;

        var hDevMode = Marshal.AllocHGlobal(devModeBytes.Length);
        try
        {
            Marshal.Copy(devModeBytes, 0, hDevMode, devModeBytes.Length);
            settings.SetHdevmode(hDevMode);
        }
        finally
        {
            Marshal.FreeHGlobal(hDevMode);
        }
    }

    private static bool TryShowDocumentProperties(IntPtr hwnd, string printerName, byte[]? inputDevMode,
        out byte[]? outputDevMode)
    {
        outputDevMode = null;

        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var size = DocumentProperties(hwnd, hPrinter, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (size < 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var outputPtr = Marshal.AllocHGlobal(size);
            IntPtr inputPtr = IntPtr.Zero;

            try
            {
                var flags = DM_IN_PROMPT | DM_OUT_BUFFER;
                if (inputDevMode is { Length: > 0 })
                {
                    inputPtr = Marshal.AllocHGlobal(inputDevMode.Length);
                    Marshal.Copy(inputDevMode, 0, inputPtr, inputDevMode.Length);
                    flags |= DM_IN_BUFFER;
                }

                var result = DocumentProperties(hwnd, hPrinter, printerName, outputPtr,
                    inputPtr, flags);

                if (result != 1) // IDOK
                    return false;

                outputDevMode = new byte[size];
                Marshal.Copy(outputPtr, outputDevMode, 0, size);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(outputPtr);
                if (inputPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(inputPtr);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    private static bool IsPrinterInstalled(string printerName)
    {
        foreach (string name in PrinterSettings.InstalledPrinters)
        {
            if (string.Equals(name, printerName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int DocumentProperties(IntPtr hwnd, IntPtr hPrinter, string pDeviceName,
        IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool OpenPrinter(string? pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);
}
