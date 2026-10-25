using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PrintMark.Core;

public record DiscoveredPrinter(
    string Name,
    string DriverName,
    string PortName,
    string IpAddress,
    string MacAddress,
    string Vendor,
    PrinterDispatchMode RecommendedMode,
    bool IsShared,
    string Status
);

public record NetworkPrinterNode(
    string IpAddress,
    string MacAddress,
    int Port = 9100
);

[SupportedOSPlatform("windows")]
public static class PrinterDiscoveryService
{
    /// <summary>
    /// Enumerates all installed Windows Print Queues, resolves their IP & MAC,
    /// and automatically detects whether each printer is Canon, HP, or Generic.
    /// </summary>
    public static List<DiscoveredPrinter> GetInstalledQueues()
    {
        var list = new List<DiscoveredPrinter>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverName, PortName, Shared, WorkOffline, PrinterStatus FROM Win32_Printer");

            foreach (ManagementObject printer in searcher.Get())
            {
                string name = printer["Name"]?.ToString() ?? "";
                string driver = printer["DriverName"]?.ToString() ?? "";
                string port = printer["PortName"]?.ToString() ?? "";
                bool shared = Convert.ToBoolean(printer["Shared"] ?? false);
                bool offline = Convert.ToBoolean(printer["WorkOffline"] ?? false);
                int rawStatus = Convert.ToInt32(printer["PrinterStatus"] ?? 3);

                string status = offline ? "Offline" : (rawStatus == 3 ? "Idle / Ready" : rawStatus == 4 ? "Printing" : "Ready");
                string ip = ResolvePortIp(port);
                string mac = !string.IsNullOrWhiteSpace(ip) ? ResolveMacFromArp(ip) : "";

                var (vendor, recommendedMode) = ClassifyVendorAndMode(name, driver);

                list.Add(new DiscoveredPrinter(
                    Name: name,
                    DriverName: driver,
                    PortName: port,
                    IpAddress: ip,
                    MacAddress: mac,
                    Vendor: vendor,
                    RecommendedMode: recommendedMode,
                    IsShared: shared,
                    Status: status
                ));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PrinterDiscovery] WMI query failed: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// Automatically determines the vendor (Canon, HP, etc.) and fastest DispatchMode.
    /// </summary>
    public static (string Vendor, PrinterDispatchMode Mode) ClassifyVendorAndMode(string queueName, string driverName)
    {
        string combined = $"{queueName} {driverName}".ToUpperInvariant();

        if (combined.Contains("CANON") || combined.Contains("UFR") || combined.Contains("MF45"))
        {
            // Canon UFR II requires Windows Driver Queue (mswinpr2)
            return ("Canon", PrinterDispatchMode.WindowsDriverQueue);
        }

        if (combined.Contains("HP") || combined.Contains("HEWLETT") || combined.Contains("LASERJET"))
        {
            // HP LaserJets support ultra-fast hardware PCL6 streaming over Port 9100
            return ("HP", PrinterDispatchMode.DirectPclSocket9100);
        }

        if (combined.Contains("MICROSOFT PS CLASS DRIVER") || combined.Contains("PROTECTED"))
        {
            return ("PrintMark Virtual Trapdoor", PrinterDispatchMode.WindowsDriverQueue);
        }

        return ("Generic", PrinterDispatchMode.WindowsDriverQueue);
    }

    /// <summary>
    /// Scans the local /24 subnet in parallel (~400ms) for physical network printers listening on TCP Port 9100.
    /// </summary>
    public static async Task<List<NetworkPrinterNode>> ScanSubnetForPrintersAsync(
        string subnetPrefix = "192.168.0.",
        int port = 9100,
        int timeoutMs = 350)
    {
        var found = new ConcurrentBag<NetworkPrinterNode>();

        var tasks = Enumerable.Range(1, 254).Select(async i =>
        {
            string ip = $"{subnetPrefix}{i}";
            try
            {
                using var tcp = new TcpClient();
                var connectTask = tcp.ConnectAsync(ip, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));

                if (completed == connectTask && tcp.Connected)
                {
                    string mac = ResolveMacFromArp(ip);
                    found.Add(new NetworkPrinterNode(ip, mac, port));
                }
            }
            catch
            {
                // Host offline or port 9100 closed
            }
        });

        await Task.WhenAll(tasks);
        return found.OrderBy(x => x.IpAddress).ToList();
    }

    /// <summary>
    /// Self-Healing Printer IP: If the physical printer's DHCP IP changes, locates the printer on the LAN
    /// by its MAC address (or falls back to the first discovered port-9100 printer) and updates the Windows Port IP.
    /// </summary>
    public static async Task<(bool Healed, string NewIp, string Message)> HealPrinterPortIpAsync(
        string backQueueName,
        string expectedPrinterMac = "",
        string subnetPrefix = "192.168.0.")
    {
        var printers = await ScanSubnetForPrintersAsync(subnetPrefix);
        if (printers.Count == 0)
        {
            return (false, "", $"No physical printers responding on port 9100 in subnet {subnetPrefix}x");
        }

        NetworkPrinterNode? match = null;
        if (!string.IsNullOrWhiteSpace(expectedPrinterMac))
        {
            string normalizedExpected = NormalizeMac(expectedPrinterMac);
            match = printers.FirstOrDefault(p => NormalizeMac(p.MacAddress) == normalizedExpected);
        }

        // If MAC wasn't specified or not in ARP cache yet, pick first discovered 9100 hardware printer
        match ??= printers.First();

        // Find the PortName assigned to backQueueName (e.g. 'MF4500 Series')
        var queue = GetInstalledQueues()
            .FirstOrDefault(q => string.Equals(q.Name, backQueueName, StringComparison.OrdinalIgnoreCase));

        if (queue == null)
        {
            return (false, match.IpAddress, $"Back queue '{backQueueName}' not found on server.");
        }

        if (string.Equals(queue.IpAddress, match.IpAddress, StringComparison.OrdinalIgnoreCase))
        {
            return (true, match.IpAddress, $"Printer '{backQueueName}' is already pointing to {match.IpAddress}.");
        }

        // Update Windows Standard TCP/IP Port in Registry and restart Spooler
        string portKeyPath = $@"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports\{queue.PortName}";
        using (var key = Registry.LocalMachine.OpenSubKey(portKeyPath, writable: true))
        {
            if (key == null)
            {
                return (false, match.IpAddress, $"Registry port key '{queue.PortName}' not found.");
            }

            key.SetValue("HostName", match.IpAddress, RegistryValueKind.String);
            key.SetValue("IPAddress", match.IpAddress, RegistryValueKind.String);
        }

        // Restart Spooler to apply new IP
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"Restart-Service Spooler -Force\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi);
        if (proc != null) await proc.WaitForExitAsync();

        return (true, match.IpAddress, $"Healed port '{queue.PortName}' for '{backQueueName}' -> {match.IpAddress} (MAC: {match.MacAddress}).");
    }

    public static string ResolvePortIp(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName)) return "";
        if (Regex.IsMatch(portName, @"^\d{1,3}(\.\d{1,3}){3}$")) return portName;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports\{portName}");
            string? host = key?.GetValue("HostName")?.ToString();
            string? ip = key?.GetValue("IPAddress")?.ToString();
            return !string.IsNullOrWhiteSpace(ip) ? ip : (host ?? "");
        }
        catch
        {
            return "";
        }
    }

    public static string ResolveMacFromArp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip == "127.0.0.1") return "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("arp", $"-a {ip}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            string output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(1500);

            var match = Regex.Match(output, @"([0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}");
            return match.Success ? NormalizeMac(match.Value) : "";
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeMac(string mac)
        => mac.Trim().ToUpperInvariant().Replace(':', '-');
}