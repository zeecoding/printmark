using System;
using System.DirectoryServices.AccountManagement;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace PrintMark.Service;

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
    // Win32 API to fetch physical MAC address from the network stack / ARP cache
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIP, uint srcIP, byte[] pMacAddr, ref uint phyAddrLen);

    public static string ResolveMacAddress(string ipAddressString)
    {
        if (ipAddressString == "127.0.0.1" || ipAddressString == "::1")
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
            // Fallback if ARP is blocked across subnets
        }

        return "UNKNOWN-MAC";
    }

    public static ResolvedIdentity ResolveContext(string clientIp, string userName, string computerName)
    {
        string macAddress = ResolveMacAddress(clientIp);
        string userSid = string.Empty;
        string userGuid = string.Empty;
        string machineSid = string.Empty;
        string machineGuid = string.Empty;
        string authType = "Kerberos";

        try
        {
            // Attempt to resolve against the active domain controller (lab.local)
            using var domainContext = new PrincipalContext(ContextType.Domain);

            // 1. Resolve User SID & ObjectGUID
            using var userPrincipal = UserPrincipal.FindByIdentity(domainContext, IdentityType.SamAccountName, userName);
            if (userPrincipal != null)
            {
                userSid = userPrincipal.Sid?.ToString() ?? string.Empty;
                userGuid = userPrincipal.Guid?.ToString() ?? string.Empty;
            }

            // 2. Resolve Computer SID & ObjectGUID (domain computer accounts end with '$')
            string machineSamName = computerName.TrimEnd('$') + "$";
            using var compPrincipal = ComputerPrincipal.FindByIdentity(domainContext, IdentityType.SamAccountName, machineSamName);
            if (compPrincipal != null)
            {
                machineSid = compPrincipal.Sid?.ToString() ?? string.Empty;
                machineGuid = compPrincipal.Guid?.ToString() ?? string.Empty;
            }
        }
        catch
        {
            // Running outside domain (local test fallback)
            try
            {
                var localUser = new NTAccount(userName);
                userSid = localUser.Translate(typeof(SecurityIdentifier)).Value;
            }
            catch
            {
                userSid = "S-1-5-21-LAB-LOCAL-DEV-USER";
            }

            machineSid = "S-1-5-21-LAB-LOCAL-DEV-MACHINE";
            userGuid = Guid.NewGuid().ToString();
            machineGuid = Guid.NewGuid().ToString();
            authType = "NTLM/Local";
        }

        return new ResolvedIdentity(
            ClientIp: clientIp,
            ClientMac: macAddress,
            UserSid: userSid,
            UserGuid: userGuid,
            MachineSid: machineSid,
            MachineGuid: machineGuid,
            AuthType: authType
        );
    }
}