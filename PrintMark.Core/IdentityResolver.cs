using System.Collections.Concurrent;
using System.Diagnostics;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;

namespace PrintMark.Core;

public record ResolvedIdentity(
    string RawUsername,
    string DisplayName,
    string Codename,
    string Department,
    string Email,
    string ComputerName,
    string ClientIp,
    bool IsDomainUser,
    long ResolutionElapsedMs
);

[SupportedOSPlatform("windows")]
public static class IdentityResolver
{
    private record CacheEntry(ResolvedIdentity Identity, DateTime Expiration);

    // In-memory thread-safe identity cache (Key: "USERNAME|COMPUTER_OR_IP")
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FastResolutionTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Resolves full identity and custom codename information with aggressive in-memory caching.
    /// </summary>
    public static async Task<ResolvedIdentity> ResolveAsync(string rawUsername, string clientEndpointOrIp)
    {
        var sw = Stopwatch.StartNew();
        string cacheKey = $"{rawUsername}|{clientEndpointOrIp}";

        // 1. Check in-memory cache first (< 1ms hit)
        if (Cache.TryGetValue(cacheKey, out var entry) && entry.Expiration > DateTime.UtcNow)
        {
            sw.Stop();
            return entry.Identity with { ResolutionElapsedMs = sw.ElapsedMilliseconds };
        }

        string cleanUsername = SanitizeUsername(rawUsername);
        string clientIp = ExtractIp(clientEndpointOrIp);

        // 2. Resolve Hostname with a strict timeout to avoid DNS / NetBIOS lag
        string resolvedComputer = await ResolveComputerNameFastAsync(clientIp, FastResolutionTimeout);

        // 3. Resolve AD attributes and codename via AccountManagement & LDAP
        var (displayName, codename, department, email, isDomain) = await ResolveDirectoryAttributesFastAsync(cleanUsername, FastResolutionTimeout);

        // 4. If AD did not yield a codename, fall back to local user_aliases.json map
        if (string.IsNullOrWhiteSpace(codename))
        {
            codename = ResolveCodenameFromJsonFallback(cleanUsername);
        }

        // 5. Final fallback to display name if no custom codename exists
        if (string.IsNullOrWhiteSpace(codename))
        {
            codename = displayName;
        }

        sw.Stop();

        var result = new ResolvedIdentity(
            RawUsername: rawUsername,
            DisplayName: displayName,
            Codename: codename,
            Department: department,
            Email: email,
            ComputerName: resolvedComputer,
            ClientIp: clientIp,
            IsDomainUser: isDomain,
            ResolutionElapsedMs: sw.ElapsedMilliseconds
        );

        // Store in cache
        Cache[cacheKey] = new CacheEntry(result, DateTime.UtcNow.Add(CacheTtl));

        return result;
    }

    /// <summary>
    /// Reads configured custom AD attribute name from PRINTMARK_AD_ATTRIBUTE env var or appsettings.json.
    /// </summary>
    private static string? GetConfiguredAdAttribute()
    {
        string? env = Environment.GetEnvironmentVariable("PRINTMARK_AD_ATTRIBUTE");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        try
        {
            string appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(appSettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
                if (doc.RootElement.TryGetProperty("PRINTMARK_AD_ATTRIBUTE", out var p1) && p1.ValueKind == JsonValueKind.String)
                    return p1.GetString();
                if (doc.RootElement.TryGetProperty("AdAttribute", out var p2) && p2.ValueKind == JsonValueKind.String)
                    return p2.GetString();
                if (doc.RootElement.TryGetProperty("PrintMark", out var pm) && pm.TryGetProperty("AdAttribute", out var p3) && p3.ValueKind == JsonValueKind.String)
                    return p3.GetString();
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Falls back to local user_aliases.json in AppContext.BaseDirectory if AD is unreachable.
    /// </summary>
    private static string ResolveCodenameFromJsonFallback(string username)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "user_aliases.json");
            if (File.Exists(path))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (map != null && map.TryGetValue(username, out var alias) && !string.IsNullOrWhiteSpace(alias))
                {
                    return alias.Trim();
                }
            }
        }
        catch { }

        return string.Empty;
    }

    /// <summary>
    /// Resolves client host/computer name, capping network queries (DNS/LLMNR/NetBIOS) to avoid hangs.
    /// </summary>
    private static async Task<string> ResolveComputerNameFastAsync(string ipAddress, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || ipAddress == "127.0.0.1" || ipAddress == "::1")
        {
            return Environment.MachineName;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var hostEntryTask = Dns.GetHostEntryAsync(ipAddress);
            var completedTask = await Task.WhenAny(hostEntryTask, Task.Delay(timeout, cts.Token));

            if (completedTask == hostEntryTask)
            {
                var entry = await hostEntryTask;
                if (!string.IsNullOrWhiteSpace(entry.HostName))
                {
                    return entry.HostName.Split('.')[0].ToUpperInvariant();
                }
            }
        }
        catch
        {
            // Reverse lookup timed out or failed (standard for workgroups)
        }

        return ipAddress;
    }

    /// <summary>
    /// Queries Active Directory via AccountManagement and LDAP with dynamic attribute fallback:
    /// Configured Attribute -> employeeID -> extensionAttribute1.
    /// </summary>
    private static async Task<(string DisplayName, string Codename, string Department, string Email, bool IsDomain)> 
        ResolveDirectoryAttributesFastAsync(string username, TimeSpan timeout)
    {
        string defaultDisplayName = username;
        string defaultDepartment = "General";
        string defaultEmail = string.Empty;
        string defaultCodename = string.Empty;

        // Quick check: If no domain joined or local admin account, bypass LDAP immediately
        if (string.Equals(username, "Administrator", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(username, "SYSTEM", StringComparison.OrdinalIgnoreCase) ||
            !IsDomainJoined())
        {
            return (defaultDisplayName, defaultCodename, defaultDepartment, defaultEmail, false);
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);

            return await Task.Run(() =>
            {
                string disp = defaultDisplayName;
                string dept = defaultDepartment;
                string mail = defaultEmail;
                string codename = string.Empty;
                string? configuredAttr = GetConfiguredAdAttribute();

                // 1. Resolve via System.DirectoryServices.AccountManagement
                try
                {
                    using var pContext = new PrincipalContext(ContextType.Domain);
                    using var userPrincipal = UserPrincipal.FindByIdentity(pContext, IdentityType.SamAccountName, username);
                    if (userPrincipal != null)
                    {
                        if (!string.IsNullOrWhiteSpace(userPrincipal.DisplayName)) disp = userPrincipal.DisplayName;
                        if (!string.IsNullOrWhiteSpace(userPrincipal.EmailAddress)) mail = userPrincipal.EmailAddress;

                        if (userPrincipal.GetUnderlyingObject() is DirectoryEntry de)
                        {
                            // A. Configured AD attribute first
                            if (!string.IsNullOrWhiteSpace(configuredAttr) && de.Properties.Contains(configuredAttr))
                            {
                                codename = de.Properties[configuredAttr].Value?.ToString() ?? string.Empty;
                            }
                            // B. Fallback to employeeID
                            if (string.IsNullOrWhiteSpace(codename) && de.Properties.Contains("employeeID"))
                            {
                                codename = de.Properties["employeeID"].Value?.ToString() ?? string.Empty;
                            }
                            // C. Fallback to extensionAttribute1
                            if (string.IsNullOrWhiteSpace(codename) && de.Properties.Contains("extensionAttribute1"))
                            {
                                codename = de.Properties["extensionAttribute1"].Value?.ToString() ?? string.Empty;
                            }
                            if (de.Properties.Contains("department"))
                            {
                                string? d = de.Properties["department"].Value?.ToString();
                                if (!string.IsNullOrWhiteSpace(d)) dept = d;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(codename) && !string.IsNullOrWhiteSpace(userPrincipal.EmployeeId))
                        {
                            codename = userPrincipal.EmployeeId;
                        }

                        if (!string.IsNullOrWhiteSpace(codename))
                        {
                            return (disp, codename, dept, mail, true);
                        }
                    }
                }
                catch
                {
                    // Fall through to DirectorySearcher if AccountManagement times out or throws
                }

                // 2. Direct LDAP DirectorySearcher fallback
                try
                {
                    using var root = new DirectoryEntry("LDAP://rootDSE");
                    string defaultNamingContext = root.Properties["defaultNamingContext"].Value?.ToString() ?? string.Empty;

                    if (!string.IsNullOrEmpty(defaultNamingContext))
                    {
                        using var searchRoot = new DirectoryEntry($"LDAP://{defaultNamingContext}");
                        using var searcher = new DirectorySearcher(searchRoot)
                        {
                            Filter = $"(&(objectClass=user)(sAMAccountName={EscapeLdapFilter(username)}))",
                            SearchScope = SearchScope.Subtree,
                            PageSize = 1
                        };

                        searcher.PropertiesToLoad.Add("displayName");
                        searcher.PropertiesToLoad.Add("department");
                        searcher.PropertiesToLoad.Add("mail");
                        if (!string.IsNullOrWhiteSpace(configuredAttr))
                            searcher.PropertiesToLoad.Add(configuredAttr);
                        searcher.PropertiesToLoad.Add("employeeID");
                        searcher.PropertiesToLoad.Add("extensionAttribute1");

                        SearchResult? result = searcher.FindOne();
                        if (result != null)
                        {
                            disp = GetProp(result, "displayName") ?? disp;
                            dept = GetProp(result, "department") ?? dept;
                            mail = GetProp(result, "mail") ?? mail;

                            if (!string.IsNullOrWhiteSpace(configuredAttr))
                                codename = GetProp(result, configuredAttr) ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(codename))
                                codename = GetProp(result, "employeeID") ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(codename))
                                codename = GetProp(result, "extensionAttribute1") ?? string.Empty;

                            return (disp, codename, dept, mail, true);
                        }
                    }
                }
                catch { }

                return (disp, codename, dept, mail, false);
            }, cts.Token);
        }
        catch
        {
            return (defaultDisplayName, defaultCodename, defaultDepartment, defaultEmail, false);
        }
    }

    private static bool IsDomainJoined()
    {
        try
        {
            var domain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
            return !string.IsNullOrWhiteSpace(domain);
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeUsername(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        int idx = raw.LastIndexOf('\\');
        return idx >= 0 ? raw[(idx + 1)..] : raw;
    }

    private static string ExtractIp(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "127.0.0.1";
        
        // Strip port if formatted as IP:PORT
        if (endpoint.Contains(':'))
        {
            int colonIndex = endpoint.LastIndexOf(':');
            return endpoint[..colonIndex];
        }

        return endpoint;
    }

    private static string? GetProp(SearchResult res, string prop)
    {
        return res.Properties.Contains(prop) && res.Properties[prop].Count > 0
            ? res.Properties[prop][0]?.ToString()
            : null;
    }

    private static string EscapeLdapFilter(string input)
    {
        return input
            .Replace("\\", "\\5c")
            .Replace("*", "\\2a")
            .Replace("(", "\\28")
            .Replace(")", "\\29")
            .Replace("\0", "\\00");
    }
}