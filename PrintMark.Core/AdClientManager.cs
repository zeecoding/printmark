using System.Collections.Concurrent;
using System.Diagnostics;
using System.DirectoryServices;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PrintMark.Core;

public record ManagedClientStatus(
    string ComputerName,
    string IpAddress,
    string MacAddress,
    string OperatingSystem,
    bool IsOnline,
    bool HasPrintMarkInstalled,
    string AssignedPrinterQueue,
    DateTimeOffset LastChecked
);

[SupportedOSPlatform("windows")]
public static class AdClientManager
{
    /// <summary>
    /// Queries Active Directory (with automatic ARP/Workgroup fallback) for all client PCs
    /// and checks which PCs have the PrintMark Protected Queue installed vs. Not Installed.
    /// </summary>
    public static async Task<List<ManagedClientStatus>> GetDomainClientInventoryAsync(
        string printServerIp = "192.168.0.200",
        string sharedQueueName = "Canon-Protected")
    {
        var discoveredHosts = new ConcurrentDictionary<string, (string Name, string Os, string HintIp)>(StringComparer.OrdinalIgnoreCase);

        // 1. Query Active Directory Domain Computers via LDAP (if running on a Domain)
        try
        {
            using var rootDse = new DirectoryEntry("LDAP://RootDSE");
            string? namingContext = rootDse.Properties["defaultNamingContext"].Value?.ToString();
            if (!string.IsNullOrWhiteSpace(namingContext))
            {
                using var searchRoot = new DirectoryEntry($"LDAP://{namingContext}");
                using var searcher = new DirectorySearcher(searchRoot)
                {
                    Filter = "(objectCategory=computer)",
                    PageSize = 500
                };
                searcher.PropertiesToLoad.Add("name");
                searcher.PropertiesToLoad.Add("dNSHostName");
                searcher.PropertiesToLoad.Add("operatingSystem");

                foreach (SearchResult result in searcher.FindAll())
                {
                    string name = result.Properties["name"].Count > 0
                        ? result.Properties["name"][0]?.ToString() ?? ""
                        : "";
                    string os = result.Properties["operatingSystem"].Count > 0
                        ? result.Properties["operatingSystem"][0]?.ToString() ?? "Windows Workstation"
                        : "Windows Workstation";

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        discoveredHosts[name] = (name, os, "");
                    }
                }
            }
        }
        catch
        {
            // Not joined to an AD domain or LDAP unavailable; fallback below handles Workgroup/LAN discovery
        }

        // 2. Always include known LAN / Lab peers from ARP cache so Workgroup PCs (like Surface Laptop) appear
        foreach (var (ip, mac) in GetActiveArpClients("192.168.0."))
        {
            if (ip == printServerIp || ip.EndsWith(".1") || ip.EndsWith(".255") || ip == "192.168.0.105")
                continue;

            string hostName = ResolveHostName(ip);
            if (!discoveredHosts.ContainsKey(hostName))
            {
                discoveredHosts[hostName] = (hostName, "Windows Client (LAN)", ip);
            }
        }

        var results = new ConcurrentBag<ManagedClientStatus>();

        await Parallel.ForEachAsync(discoveredHosts.Values, async (comp, ct) =>
        {
            string ip = comp.HintIp;
            bool online = false;
            bool installed = false;

            try
            {
                using var ping = new Ping();
                string target = !string.IsNullOrWhiteSpace(comp.HintIp) ? comp.HintIp : comp.Name;
                var reply = await ping.SendPingAsync(target, 500);
                if (reply.Status == IPStatus.Success)
                {
                    online = true;
                    ip = reply.Address.ToString();
                    installed = CheckRemoteClientInstalled(comp.Name, ip, printServerIp, sharedQueueName);
                }
            }
            catch
            {
                online = false;
            }

            string mac = !string.IsNullOrWhiteSpace(ip) ? PrinterDiscoveryService.ResolveMacFromArp(ip) : "";

            results.Add(new ManagedClientStatus(
                ComputerName: comp.Name,
                IpAddress: string.IsNullOrWhiteSpace(ip) ? "Offline" : ip,
                MacAddress: mac,
                OperatingSystem: comp.Os,
                IsOnline: online,
                HasPrintMarkInstalled: installed,
                AssignedPrinterQueue: installed ? $@"\\{printServerIp}\{sharedQueueName}" : "Not Installed",
                LastChecked: DateTimeOffset.UtcNow
            ));
        });

        return results
            .OrderByDescending(x => x.IsOnline)
            .ThenByDescending(x => x.HasPrintMarkInstalled)
            .ThenBy(x => x.ComputerName)
            .ToList();
    }

    /// <summary>
    /// Checks whether a remote client PC has the PrintMark Protected Queue installed.
    /// </summary>
    private static bool CheckRemoteClientInstalled(string computerName, string ip, string serverIp, string queueName)
    {
        try
        {
            string target = !string.IsNullOrWhiteSpace(ip) ? ip : computerName;
            using var reg = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, target);

            // Check 1: Explicit PrintMarkClient marker key
            using (var pmKey = reg.OpenSubKey(@"SOFTWARE\PrintMarkClient"))
            {
                string? val = pmKey?.GetValue("InstalledQueue")?.ToString();
                if (!string.IsNullOrWhiteSpace(val) && val.Contains(queueName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Check 2: Global per-machine printer connections in Windows Spooler registry
            using (var connKey = reg.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Connections"))
            {
                if (connKey != null)
                {
                    foreach (string sub in connKey.GetSubKeyNames())
                    {
                        if (sub.Contains(queueName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
        }
        catch
        {
            // Remote Registry service may be stopped on workgroup laptops; check if this machine has printed recently
        }

        return false;
    }

    /// <summary>
    /// Remotely installs the PrintMark Protected Queue onto a target Active Directory / LAN computer
    /// globally for ALL users on that PC (/ga) and sets the RPC privacy registry fix.
    /// </summary>
    public static async Task<(bool Success, string Message)> RemoteInstallClientAsync(
        string targetComputerOrIp,
        string printServerIp = "192.168.0.200",
        string sharedQueueName = "Canon-Protected",
        string? adminUser = null,
        string? adminPassword = null)
    {
        string uncQueue = $@"\\{printServerIp}\{sharedQueueName}";

        string credBlock = "";
        if (!string.IsNullOrWhiteSpace(adminUser) && !string.IsNullOrWhiteSpace(adminPassword))
        {
            credBlock = $@"
                $secpw = ConvertTo-SecureString '{Q(adminPassword)}' -AsPlainText -Force;
                $cred = New-Object System.Management.Automation.PSCredential ('{Q(adminUser)}', $secpw);
            ";
        }

        string invokeParams = !string.IsNullOrWhiteSpace(credBlock)
            ? $"-ComputerName '{Q(targetComputerOrIp)}' -Credential $cred"
            : $"-ComputerName '{Q(targetComputerOrIp)}'";

        string psCommand = $@"
            $ErrorActionPreference = 'Stop';
            {credBlock}
            Invoke-Command {invokeParams} -ScriptBlock {{
                New-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Print' -Name 'RpcAuthnLevelPrivacyEnabled' -Value 0 -PropertyType DWord -Force | Out-Null;
                Restart-Service Spooler -Force;
                rundll32.exe printui.dll,PrintUIEntry /ga /n '{Q(uncQueue)}';
                Restart-Service Spooler -Force;
                New-Item -Path 'HKLM:\SOFTWARE\PrintMarkClient' -Force | Out-Null;
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\PrintMarkClient' -Name 'InstalledQueue' -Value '{uncQueue}';
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\PrintMarkClient' -Name 'InstalledAt' -Value (Get-Date -Format o);
            }}
        ";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(psCommand))}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start PowerShell.");
            string stdErr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                return (false, $"Remote deployment to {targetComputerOrIp} failed: {stdErr.Trim()}");
            }

            return (true, $"Successfully deployed {uncQueue} globally to {targetComputerOrIp}.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Generates an Active Directory Group Policy (GPO) Computer Startup Script
    /// so Domain Controllers can automatically install PrintMark on 100s of PCs at boot.
    /// </summary>
    public static string GenerateAdGpoStartupScript(
        string outputScriptPath,
        string printServerIp = "192.168.0.200",
        string sharedQueueName = "Canon-Protected")
    {
        string uncQueue = $@"\\{printServerIp}\{sharedQueueName}";
        string scriptContent = $@"# =====================================================================
# PrintMark Enterprise - Active Directory GPO Computer Startup Script
# Assign under: Computer Configuration -> Policies -> Windows Settings -> Scripts (Startup)
# =====================================================================
$ErrorActionPreference = 'SilentlyContinue'
$ServerQueue = '{uncQueue}'

# 1. Apply Windows RPC Print Spooler Compatibility Fix
New-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Print' -Name 'RpcAuthnLevelPrivacyEnabled' -Value 0 -PropertyType DWord -Force | Out-Null

# 2. Globally map the PrintMark Protected Queue for ALL users on this workstation (/ga)
rundll32.exe printui.dll,PrintUIEntry /ga /n ""$ServerQueue""
Restart-Service Spooler -Force

# 3. Stamp local registry for PrintMark AD Inventory Tracking
New-Item -Path 'HKLM:\SOFTWARE\PrintMarkClient' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\PrintMarkClient' -Name 'InstalledQueue' -Value $ServerQueue
Set-ItemProperty -Path 'HKLM:\SOFTWARE\PrintMarkClient' -Name 'ComputerName' -Value $env:COMPUTERNAME
Set-ItemProperty -Path 'HKLM:\SOFTWARE\PrintMarkClient' -Name 'InstalledAt' -Value (Get-Date -Format o)
";
        File.WriteAllText(outputScriptPath, scriptContent);
        return outputScriptPath;
    }

    private static string Q(string s) => s.Replace("'", "''");

    private static List<(string Ip, string Mac)> GetActiveArpClients(string subnetPrefix)
    {
        var list = new List<(string Ip, string Mac)>();
        try
        {
            using var p = Process.Start(new ProcessStartInfo("arp", "-a")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            string output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(1500);

            foreach (string line in output.Split('\n'))
            {
                var ipMatch = Regex.Match(line, @"(\d{1,3}(?:\.\d{1,3}){3})");
                var macMatch = Regex.Match(line, @"([0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}");
                if (ipMatch.Success && macMatch.Success && ipMatch.Value.StartsWith(subnetPrefix))
                {
                    list.Add((ipMatch.Value, macMatch.Value.ToUpperInvariant().Replace(':', '-')));
                }
            }
        }
        catch { }
        return list;
    }

    private static string ResolveHostName(string ip)
    {
        try
        {
            var entry = System.Net.Dns.GetHostEntry(ip);
            if (!string.IsNullOrWhiteSpace(entry.HostName))
            {
                return entry.HostName.Split('.')[0].ToUpperInvariant();
            }
        }
        catch { }
        return $"PC-{ip.Replace('.', '-')}";
    }
}