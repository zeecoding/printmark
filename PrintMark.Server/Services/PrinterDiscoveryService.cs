using System.Collections.Concurrent;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace PrintMark.Server.Services;

public record DiscoveredPrinter(
    string Name,
    string IpAddress,
    int Port,
    string Driver,
    string Vendor,
    string Capability,
    bool IsOnline
);

[SupportedOSPlatform("windows")]
public class PrinterDiscoveryService
{
    /// <summary>
    /// Auto-detects the active IPv4 subnet prefix (e.g. "192.168.1" or "10.0.0")
    /// by locating the network interface holding the active default gateway.
    /// </summary>
    public static string DetectActiveSubnet()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                var ipProps = ni.GetIPProperties();
                bool hasGateway = ipProps.GatewayAddresses.Any(g => 
                    g.Address.AddressFamily == AddressFamily.InterNetwork && 
                    !g.Address.Equals(IPAddress.Any));

                if (!hasGateway) continue;

                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && 
                        !IPAddress.IsLoopback(unicast.Address))
                    {
                        byte[] bytes = unicast.Address.GetAddressBytes();
                        return $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
                    }
                }
            }
        }
        catch { }

        return "192.168.1"; // Safe fallback
    }

    public async Task<(string SubnetUsed, List<DiscoveredPrinter> Printers)> DiscoverNetworkPrintersAsync(
        string? targetSubnet = null, 
        int timeoutMs = 200)
    {
        string subnet = string.IsNullOrWhiteSpace(targetSubnet) || targetSubnet == "auto"
            ? DetectActiveSubnet() 
            : targetSubnet.Trim();

        var discovered = new ConcurrentBag<DiscoveredPrinter>();

        // 1. Enumerate local Windows Print Spooler Queues
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PortName, DriverName FROM Win32_Printer");
            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "Unknown";
                string port = obj["PortName"]?.ToString() ?? "Local";
                string driver = obj["DriverName"]?.ToString() ?? "Generic";

                string vendor = driver.Contains("Canon", StringComparison.OrdinalIgnoreCase) ? "Canon" :
                                driver.Contains("HP", StringComparison.OrdinalIgnoreCase) ? "HP" : "Generic";
                string cap = driver.Contains("UFR", StringComparison.OrdinalIgnoreCase) ? "Driver Queue (UFRII)" : "PCL / Direct";

                discovered.Add(new DiscoveredPrinter(name, port, 9100, driver, vendor, cap, true));
            }
        }
        catch { }

        // 2. Fast Asynchronous Subnet Sweep on TCP Port 9100
        var scanTasks = Enumerable.Range(1, 254).Select(async host =>
        {
            string ip = $"{subnet}.{host}";
            using var tcp = new TcpClient();
            try
            {
                var connectTask = tcp.ConnectAsync(ip, 9100);
                if (await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) == connectTask && tcp.Connected)
                {
                    if (!discovered.Any(d => d.IpAddress == ip))
                    {
                        discovered.Add(new DiscoveredPrinter(
                            Name: $"Network Device ({ip})",
                            IpAddress: ip,
                            Port: 9100,
                            Driver: "Raw JetDirect 9100",
                            Vendor: "Enterprise Network Printer",
                            Capability: "Direct PDF/PCL Socket 9100",
                            IsOnline: true
                        ));
                    }
                }
            }
            catch { }
        });

        await Task.WhenAll(scanTasks);
        return (subnet, discovered.OrderBy(p => p.IpAddress).ToList());
    }
}