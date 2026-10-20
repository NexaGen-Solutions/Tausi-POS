using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tausi.Infrastructure.Printing;

/// <summary>
/// Raw printing through the Windows spooler (winspool.drv): OpenPrinter, StartDocPrinter with
/// datatype "RAW", WritePrinter. The ESC/POS bytes reach the printer unchanged. Printer names can be
/// local ("POS-80") or shared ("\\PC\Share").
/// </summary>
[SupportedOSPlatform("windows")]
public static class WinSpool
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        public string DocName;
        public string? OutputFile;
        public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string printerName, out IntPtr printer, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr printer, int level, ref DocInfo1 docInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr printer, byte[] bytes, int count, out int written);

    public static void PrintRaw(string printerName, byte[] data, string documentName)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero)) throw Error($"Can't open {printerName}");
        try
        {
            var doc = new DocInfo1 { DocName = documentName, OutputFile = null, DataType = "RAW" };
            if (StartDocPrinter(handle, 1, ref doc) == 0) throw Error($"{printerName} refused the print job");
            try
            {
                if (!StartPagePrinter(handle)) throw Error($"{printerName} refused the print job");
                if (!WritePrinter(handle, data, data.Length, out var written) || written != data.Length)
                    throw Error($"Not everything reached {printerName}");
                EndPagePrinter(handle);
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>Throws when there is no such printer (or it can't be opened).</summary>
    public static void CheckExists(string printerName)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero)) throw Error($"Can't find the printer {printerName}");
        ClosePrinter(handle);
    }

    private static Application.Printing.PrinterTransportException Error(string what) =>
        new($"{what}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
}
