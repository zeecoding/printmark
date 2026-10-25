using System.IO;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace PrintMark.Core;

public enum PrinterDispatchMode
{
    AutoDetect = 0,
    WindowsDriverQueue = 1,
    DirectPdfSocket9100 = 2,
    DirectPclSocket9100 = 3
}

[SupportedOSPlatform("windows")]
public static class HybridPrinterDispatcher
{
    private const int RenderTimeoutMs = 180_000;

    /// <param name="renderDpi">Resolution for the Ghostscript driver path. Lower = faster (200 is fine for text documents).</param>
    /// <param name="sumatraPath">Optional path to SumatraPDF.exe. If present it is used for the driver-queue path instead of Ghostscript.</param>
    public static async Task DispatchStampedPdfAsync(
        string gsExePath,
        string stampedPdfPath,
        string backQueueName,
        string printerIp,
        int printerPort = 9100,
        PrinterDispatchMode mode = PrinterDispatchMode.AutoDetect,
        string expectedPrinterMac = "",
        int renderDpi = 300,
        string? sumatraPath = null)
    {
        if (mode == PrinterDispatchMode.AutoDetect)
        {
            var (_, recommended) = PrinterDiscoveryService.ClassifyVendorAndMode(backQueueName, backQueueName);
            mode = recommended;
        }

        try
        {
            await ExecuteDispatchCoreAsync(gsExePath, stampedPdfPath, backQueueName, printerIp, printerPort, mode, renderDpi, sumatraPath);
        }
        // Only a failed socket connection means "printer moved". A rendering failure must NOT trigger
        // a retry (it could print the job twice) and must not trigger a subnet scan.
        catch (SocketException) when (!string.IsNullOrWhiteSpace(printerIp))
        {
            string subnet = GetSubnetPrefix(printerIp);
            var (healed, newIp, _) = await PrinterDiscoveryService.HealPrinterPortIpAsync(backQueueName, expectedPrinterMac, subnet);
            if (healed && !string.IsNullOrWhiteSpace(newIp) && !string.Equals(newIp, printerIp, StringComparison.OrdinalIgnoreCase))
            {
                await ExecuteDispatchCoreAsync(gsExePath, stampedPdfPath, backQueueName, newIp, printerPort, mode, renderDpi, sumatraPath);
            }
            else
            {
                throw;
            }
        }
    }

    private static async Task ExecuteDispatchCoreAsync(
        string gsExePath,
        string stampedPdfPath,
        string backQueueName,
        string printerIp,
        int printerPort,
        PrinterDispatchMode mode,
        int renderDpi,
        string? sumatraPath)
    {
        // MODE A: Direct PDF over TCP Port 9100 (printers that understand PDF natively)
        if (mode == PrinterDispatchMode.DirectPdfSocket9100 && !string.IsNullOrWhiteSpace(printerIp))
        {
            await SendFileToSocketAsync(stampedPdfPath, printerIp, printerPort);
            return;
        }

        // MODE B: Direct hardware PCL6 over TCP Port 9100 (HP-style printers like HP LaserJet P2055dn)
        if (mode == PrinterDispatchMode.DirectPclSocket9100 && !string.IsNullOrWhiteSpace(printerIp))
        {
            string pclTemp = stampedPdfPath + ".pcl";
            try
            {
                // Dynamic page sizing via -dPDFFitPage and -dAutoRotatePages=/None (no hardcoded A4)
                var (code, output) = await ProcessRunner.RunAsync(gsExePath,
                    $"-q -dBATCH -dNOPAUSE -dSAFER -dPDFFitPage -dAutoRotatePages=/None -dNumRenderingThreads=4 -r{renderDpi} -sDEVICE=pxlmono -sOutputFile=\"{pclTemp}\" \"{stampedPdfPath}\"",
                    RenderTimeoutMs, highPriority: true);

                if (code != 0 || !File.Exists(pclTemp))
                    throw new InvalidOperationException($"Ghostscript PCL6 generation failed: {output}");

                await SendFileToSocketAsync(pclTemp, printerIp, printerPort);
            }
            finally
            {
                try { File.Delete(pclTemp); } catch { }
            }
            return;
        }

        // MODE C: Windows driver queue (host-based printers such as Canon UFR II LT)
        if (!string.IsNullOrWhiteSpace(sumatraPath) && File.Exists(sumatraPath))
        {
            var (code, output) = await ProcessRunner.RunAsync(sumatraPath,
                $"-print-to \"{backQueueName}\" -print-settings \"fit,monochrome\" -silent \"{stampedPdfPath}\"",
                RenderTimeoutMs);
            if (code != 0)
                throw new InvalidOperationException($"SumatraPDF failed printing to '{backQueueName}'. ExitCode={code}, Output={output}");
            return;
        }

        {
            // Dynamic page sizing via -dPDFFitPage and -dAutoRotatePages=/None (no hardcoded A4 or -dFIXEDMEDIA)
            // to allow A4, US Letter, and US Legal to print without driver halts or clipped edges.
            var (code, output) = await ProcessRunner.RunAsync(gsExePath,
                $"-q -dBATCH -dNOPAUSE -dSAFER -dPDFFitPage -dAutoRotatePages=/None -dPrinted -dNumRenderingThreads=4 -r{renderDpi} -sDEVICE=mswinpr2 -sOutputFile=\"%printer%{backQueueName}\" \"{stampedPdfPath}\"",
                RenderTimeoutMs, highPriority: true);

            if (code != 0)
                throw new InvalidOperationException($"Ghostscript failed printing to '{backQueueName}'. ExitCode={code}, Error={output}");
        }
    }

    private static async Task SendFileToSocketAsync(string path, string ip, int port)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ip, port);
        await using var net = tcp.GetStream();
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        await file.CopyToAsync(net, 65536);
    }

    private static string GetSubnetPrefix(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return "192.168.0.";
        int lastDot = ip.LastIndexOf('.');
        return lastDot > 0 ? ip[..(lastDot + 1)] : "192.168.0.";
    }
}