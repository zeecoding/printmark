using System;
using System.DirectoryServices.AccountManagement;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PrintMark.Service;

public record SpoolJobInfo(
    int JobId,
    string UserName,
    string ComputerName,
    string DocumentName
);

public record ResolvedIdentity(
    string ClientIp,
    string ClientMac,
    string UserSid,
    string UserGuid,
    string MachineSid,
    string MachineGuid,
    string AuthType
);

public static class IdentityResolver
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIP, uint srcIP, byte[] pMacAddr, ref uint phyAddrLen);

    public static SpoolJobInfo ResolveActiveSpoolJob(string frontQueueName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT JobId, Name, Owner, HostPrintQueue, Document, JobStatus, StatusMask FROM Win32_PrintJob");

            foreach (ManagementObject job in searcher.Get())
            {
                string jobQueueName = job["Name"]?.ToString() ?? string.Empty; // Format: "Canon-Protected, 14"
                if (!string.IsNullOrWhiteSpace(frontQueueName) &&
                    !jobQueueName.StartsWith(frontQueueName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int jobId = Convert.ToInt32(job["JobId"] ?? 1);
                string owner = job["Owner"]?.ToString() ?? Environment.UserName;
                string host = job["HostPrintQueue"]?.ToString() ?? Environment.MachineName;
                string doc = job["Document"]?.ToString() ?? "PrintJob";

                return new SpoolJobInfo(
                    JobId: jobId,
                    UserName: CleanUserName(owner),
                    ComputerName: SanitizeHostName(host),
                    DocumentName: doc
                );
            }
        }
        catch
        {
            // Fallback if WMI query fails
        }

        return new SpoolJobInfo(
            JobId: (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000),
            UserName: Environment.UserName,
            ComputerName: Environment.MachineName,
            DocumentName: "LocalJob"
        );
    }

    public static string SanitizeHostName(string rawComputerName)
    {
        if (string.IsNullOrWhiteSpace(rawComputerName)) return "UNKNOWN-HOST";
        return rawComputerName.TrimStart('\\').Split('.')[0].TrimEnd('$').Trim();
    }

    public static string CleanUserName(string rawUserName)
    {
        if (string.IsNullOrWhiteSpace(rawUserName)) return "UNKNOWN-USER";
        return rawUserName.Contains('\\') ? rawUserName.Split('\\').Last().Trim() : rawUserName.Trim();
    }

    public static string ResolveClientIpv4(string socketIp, string computerName)
    {
        // 1. If socketIp is already a real non-loopback LAN IPv4, use it
        if (!string.IsNullOrWhiteSpace(socketIp) && socketIp != "127.0.0.1" && socketIp != "::1")
        {
            return socketIp;
        }

        // 2. If computerName in spooler is already an IPv4 address (e.g. \\192.168.0.105)
        string cleanHost = SanitizeHostName(computerName);
        if (IPAddress.TryParse(cleanHost, out var parsedHostIp) && !IPAddress.IsLoopback(parsedHostIp))
        {
            return parsedHostIp.ToString();
        }

        // 3. Resolve client computerName via DNS / LLMNR / NetBIOS
        try
        {
            if (!string.Equals(cleanHost, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                var addresses = Dns.GetHostAddresses(cleanHost);
                var lanIp = addresses.FirstOrDefault(a =>
                    a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                if (lanIp != null)
                {
                    return lanIp.ToString();
                }
            }
        }
        catch
        {
            // Ignore DNS failure and fall through to SMB port 445 inspection
        }

        // 4. Fallback: Inspect active SMB (Port 445) TCP connections from remote clients to this Print Server
        try
        {
            var activeConns = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
            var smbClient = activeConns.FirstOrDefault(c =>
                c.State == TcpState.Established &&
                c.LocalEndPoint.Port == 445 &&
                c.RemoteEndPoint.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(c.RemoteEndPoint.Address));

            if (smbClient != null)
            {
                return smbClient.RemoteEndPoint.Address.ToString();
            }
        }
        catch
        {
            // Ignore TCP table inspection errors
        }

        // 5. Final fallback if printing locally on the same machine: get local physical NIC IPv4
        try
        {
            var localIp = Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (localIp != null)
            {
                return localIp.ToString();
            }
        }
        catch { }

        return "127.0.0.1";
    }

    public static string ResolveMacAddress(string ipAddressString)
    {
        if (string.IsNullOrWhiteSpace(ipAddressString) || ipAddressString == "127.0.0.1" || ipAddressString == "::1")
        {
            return "00-00-00-00-00-00 (Loopback)";
        }

        try
        {
            if (IPAddress.TryParse(ipAddressString, out var ip))
            {
                byte[] ipBytes = ip.GetAddressBytes();
                if (ipBytes.Length == 4)
                {
                    uint destIp = BitConverter.ToUInt32(ipBytes, 0);
                    byte[] macAddr = new byte[6];
                    uint macAddrLen = (uint)macAddr.Length;

                    int result = SendARP(destIp, 0, macAddr, ref macAddrLen);
                    if (result == 0 && macAddrLen == 6)
                    {
                        return BitConverter.ToString(macAddr, 0, 6);
                    }
                }
            }
        }
        catch
        {
            // Fallback if ARP is blocked
        }

        return "UNKNOWN-MAC";
    }

    public static ResolvedIdentity ResolveContext(string socketIp, string userName, string computerName)
    {
        string cleanHost = SanitizeHostName(computerName);
        string cleanUser = CleanUserName(userName);

        string resolvedIp = ResolveClientIpv4(socketIp, cleanHost);
        string macAddress = ResolveMacAddress(resolvedIp);

        string userSid = string.Empty;
        string userGuid = string.Empty;
        string machineSid = string.Empty;
        string machineGuid = string.Empty;
        string authType = "Kerberos";

        try
        {
            using var domainContext = new PrincipalContext(ContextType.Domain);

            using var userPrincipal = UserPrincipal.FindByIdentity(domainContext, IdentityType.SamAccountName, cleanUser);
            if (userPrincipal != null)
            {
                userSid = userPrincipal.Sid?.ToString() ?? string.Empty;
                userGuid = userPrincipal.Guid?.ToString() ?? string.Empty;
            }

            string machineSamName = cleanHost + "$";
            using var compPrincipal = ComputerPrincipal.FindByIdentity(domainContext, IdentityType.SamAccountName, machineSamName);
            if (compPrincipal != null)
            {
                machineSid = compPrincipal.Sid?.ToString() ?? string.Empty;
                machineGuid = compPrincipal.Guid?.ToString() ?? string.Empty;
            }
        }
        catch
        {
            try
            {
                var localUser = new NTAccount(cleanUser);
                userSid = localUser.Translate(typeof(SecurityIdentifier)).Value;
            }
            catch
            {
                userSid = $"S-1-5-21-WORKGROUP-{cleanUser.ToUpperInvariant()}";
            }

            machineSid = $"S-1-5-21-WORKGROUP-{cleanHost.ToUpperInvariant()}";
            userGuid = CreateDeterministicGuid($"USER:{cleanUser}").ToString();
            machineGuid = CreateDeterministicGuid($"HOST:{cleanHost}").ToString();
            authType = "NTLM/Workgroup";
        }

        return new ResolvedIdentity(
            ClientIp: resolvedIp,
            ClientMac: macAddress,
            UserSid: userSid,
            UserGuid: userGuid,
            MachineSid: machineSid,
            MachineGuid: machineGuid,
            AuthType: authType
        );
    }

    private static Guid CreateDeterministicGuid(string input)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(input.ToUpperInvariant()));
        return new Guid(hash);
    }
}