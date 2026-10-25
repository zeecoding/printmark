using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PrintMark.Core;
using PrintMark.Data;

namespace PrintMark.Client;

[SupportedOSPlatform("windows")]
public class Program
{
    private static string _serverUrl = "http://127.0.0.1:5000";
    private static string _apiKey = "demo-key-2026";
    private static string _masterKey = "change-me-demo-secret";

    // Per-queue lock dictionary so 500 different printers can dispatch in parallel without blocking each other
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _queueLocks = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly string PolicyCachePath = Path.Combine(AppContext.BaseDirectory, "watermark_policy.json");
    private static WatermarkConfig _cachedPolicy = new();
    private static DateTimeOffset _lastPolicyFetch = DateTimeOffset.MinValue;

    static Program()
    {
        // Load cached policy from AppContext.BaseDirectory immediately on boot for offline resilience
        try
        {
            if (File.Exists(PolicyCachePath))
            {
                string json = File.ReadAllText(PolicyCachePath);
                var cached = JsonSerializer.Deserialize<WatermarkConfig>(json);
                if (cached != null)
                {
                    _cachedPolicy = cached;
                }
            }
        }
        catch { }
    }

    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--test", StringComparison.OrdinalIgnoreCase))
        {
            await RunMultiDesignOfflineTestAsync();
            return;
        }

        Console.WriteLine("[*] PrintMark Enterprise Client Daemon starting...");

        _serverUrl = Environment.GetEnvironmentVariable("PRINTMARK_SERVER_URL") ?? _serverUrl;
        _apiKey = Environment.GetEnvironmentVariable("PRINTMARK_API_KEY") ?? _apiKey;
        _masterKey = Environment.GetEnvironmentVariable("PRINTMARK_MASTER_KEY") ?? _masterKey;

        string interceptQueue = Environment.GetEnvironmentVariable("PRINTMARK_INTERCEPT_QUEUE")
            ?? Environment.GetEnvironmentVariable("PRINTMARK_FRONT_QUEUE")
            ?? "PrintMark-Protected";

        int listenPort = 9101;
        string backQueueName = "";
        string printerIp = "";
        PrinterDispatchMode dispatchMode = PrinterDispatchMode.AutoDetect;

        if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.OrdinalIgnoreCase))
        {
            backQueueName = args[0];
            printerIp = args.Length > 1 ? args[1] : "";
            if (args.Length > 2 && int.TryParse(args[2], out int p)) listenPort = p;

            string driverName = DetectPrinterDriver(backQueueName);
            var (vendor, mode) = ResolveVendorAndMode(driverName);
            dispatchMode = mode;
            Console.WriteLine($"[√] Explicit back queue '{backQueueName}' uses driver '{driverName}' -> {vendor}, dispatch mode {dispatchMode}");
        }
        else
        {
            if (args.Length > 1 && int.TryParse(args[1], out int p)) listenPort = p;
            var detected = await AutoDetectPhysicalPrinterAsync(interceptQueue, listenPort);
            backQueueName = detected.QueueName;
            printerIp = detected.PrinterIp;
            dispatchMode = detected.Mode;
        }

        string serverApiLogUrl = $"{_serverUrl.TrimEnd('/')}/api/audit/log";

        EnsurePrintServiceOperationalLog();

        string gsExePath = ResolveGhostscriptExecutable();
        Console.WriteLine($"[√] Renderer: Ghostscript @ 300 dpi ({gsExePath}), audit server: {serverApiLogUrl}");

        long warmupMs = WarmUpEngines(gsExePath);
        Console.WriteLine($"[√] Warm-up finished in {warmupMs} ms");

        var listener = new TcpListener(IPAddress.Any, listenPort);
        listener.Start();
        Console.WriteLine($"[√] Listening for spools on port {listenPort} (interception queue '{interceptQueue}') -> {printerIp} ({backQueueName})");

        var localDb = new AuditDatabase(Path.Combine(AppContext.BaseDirectory, "printmark_client_audit.db"));

        while (true)
        {
            try
            {
                var tcpClient = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandlePrintStreamAsync(
                    tcpClient, backQueueName, printerIp, serverApiLogUrl, localDb, gsExePath, dispatchMode));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] TCP listener error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Auto-detects the host PC's primary physical printer queue (USB or LAN).
    /// Filters out standard Windows system virtual queues and dynamically excludes the target interception queue.
    /// If no physical printer is found, polls every 15 seconds without crashing.
    /// </summary>
    private static async Task<(string QueueName, string PrinterIp, PrinterDispatchMode Mode)> AutoDetectPhysicalPrinterAsync(
        string interceptQueue, int listenPort)
    {
        string[] systemVirtualNames = { "PDF", "XPS", "OneNote", "Fax" };
        string[] systemVirtualPorts = { "PORTPROMPT:", "FILE:", "NUL:" };

        var excludedNames = new List<string>(systemVirtualNames);
        if (!string.IsNullOrWhiteSpace(interceptQueue))
        {
            excludedNames.Add(interceptQueue);
        }

        while (true)
        {
            Console.WriteLine("[*] Auto-detecting physical printer hardware via WMI...");
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DriverName, PortName, Default, WorkOffline, PrinterStatus FROM Win32_Printer");

                var candidates = new List<(string Name, string Driver, string Port, bool IsDefault, bool WorkOffline)>();

                foreach (ManagementObject printer in searcher.Get())
                {
                    string name = printer["Name"]?.ToString() ?? "";
                    string driver = printer["DriverName"]?.ToString() ?? "";
                    string port = printer["PortName"]?.ToString() ?? "";
                    bool isDefault = Convert.ToBoolean(printer["Default"] ?? false);
                    bool offline = Convert.ToBoolean(printer["WorkOffline"] ?? false);

                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Filter out system virtual drivers & configured interception queue
                    if (excludedNames.Any(ex => name.Contains(ex, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Filter out virtual port patterns
                    if (systemVirtualPorts.Any(vp => port.StartsWith(vp, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Filter out capture ports
                    if (port.Contains("9101") || port.Contains(listenPort.ToString()))
                        continue;

                    candidates.Add((name, driver, port, isDefault, offline));
                }

                if (candidates.Count > 0)
                {
                    // Select default online printer, or any online printer, or first candidate
                    var selected = candidates.FirstOrDefault(c => c.IsDefault && !c.WorkOffline);
                    if (string.IsNullOrEmpty(selected.Name))
                        selected = candidates.FirstOrDefault(c => !c.WorkOffline);
                    if (string.IsNullOrEmpty(selected.Name))
                        selected = candidates.First();

                    string ip = PrinterDiscoveryService.ResolvePortIp(selected.Port);
                    var (vendor, mode) = ResolveVendorAndMode(selected.Driver);

                    Console.WriteLine($"[√] Auto-detected physical printer: '{selected.Name}'");
                    Console.WriteLine($"    Driver: '{selected.Driver}' -> {vendor}, Mode: {mode}, Port: '{selected.Port}'" +
                        (!string.IsNullOrWhiteSpace(ip) ? $", IP: {ip}" : " (Local/USB)"));

                    return (selected.Name, ip, mode);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] WMI query failed: {ex.Message}");
            }

            Console.WriteLine("[!] No physical printer queue detected. Waiting 15 seconds for printer hardware...");
            await Task.Delay(15_000);
        }
    }

    /// <summary>
    /// Fetches the active watermark policy from the central server and caches it to watermark_policy.json in AppContext.BaseDirectory.
    /// Falls back cleanly to the cached disk policy if the server or network is unreachable.
    /// </summary>
    private static async Task<WatermarkConfig> GetActiveWatermarkPolicyAsync()
    {
        if ((DateTimeOffset.UtcNow - _lastPolicyFetch).TotalSeconds < 5)
            return _cachedPolicy;

        try
        {
            var policy = await _http.GetFromJsonAsync<WatermarkConfig>($"{_serverUrl.TrimEnd('/')}/api/watermark/config");
            if (policy != null)
            {
                _cachedPolicy = policy;
                _lastPolicyFetch = DateTimeOffset.UtcNow;

                // Persist active policy to local disk cache using AppContext.BaseDirectory
                try
                {
                    string json = JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(PolicyCachePath, json);
                }
                catch { }

                return _cachedPolicy;
            }
        }
        catch
        {
            // Server or network unreachable - load from local disk cache if available
            try
            {
                if (File.Exists(PolicyCachePath))
                {
                    string json = await File.ReadAllTextAsync(PolicyCachePath);
                    var cached = JsonSerializer.Deserialize<WatermarkConfig>(json);
                    if (cached != null)
                    {
                        _cachedPolicy = cached;
                        return _cachedPolicy;
                    }
                }
            }
            catch { }
        }

        return _cachedPolicy;
    }

    private static async Task RunMultiDesignOfflineTestAsync()
    {
        Console.WriteLine("[*] Generating multi-format & multi-design test PDF on Desktop...");
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string src = Path.Combine(desktop, "test_formats_source.pdf");
        string dst = Path.Combine(desktop, "test_formats_stamped.pdf");

        using (var doc = new PdfSharpCore.Pdf.PdfDocument())
        {
            var p1 = doc.AddPage(); p1.Size = PdfSharpCore.PageSize.A4; p1.Orientation = PdfSharpCore.PageOrientation.Portrait;
            var p2 = doc.AddPage(); p2.Size = PdfSharpCore.PageSize.A4; p2.Orientation = PdfSharpCore.PageOrientation.Landscape;
            var p3 = doc.AddPage(); p3.Size = PdfSharpCore.PageSize.Letter; p3.Orientation = PdfSharpCore.PageOrientation.Portrait;
            var p4 = doc.AddPage(); p4.Size = PdfSharpCore.PageSize.Legal; p4.Orientation = PdfSharpCore.PageOrientation.Portrait;
            var p5 = doc.AddPage(); p5.Size = PdfSharpCore.PageSize.A3; p5.Orientation = PdfSharpCore.PageOrientation.Landscape;
            doc.Save(src);
        }

        var identity = await IdentityResolver.ResolveAsync(Environment.UserName, "127.0.0.1");
        string userCodename = !string.IsNullOrWhiteSpace(identity.Codename) ? identity.Codename : identity.DisplayName;
        var meta = new StampMetadata(105, userCodename, Environment.MachineName, DateTimeOffset.UtcNow);
        var config = await GetActiveWatermarkPolicyAsync();
        var (shortCode, _, pages) = WatermarkEngine.ApplyWatermark(src, dst, meta, config);

        Console.WriteLine($"[√] Stamped {pages} pages for {userCodename} -> {dst}");
        Console.WriteLine($"[√] ShortCode: {shortCode}");
    }

    private static async Task HandlePrintStreamAsync(
        TcpClient client,
        string backQueueName,
        string printerIp,
        string serverApiUrl,
        AuditDatabase database,
        string gsExePath,
        PrinterDispatchMode dispatchMode)
    {
        var totalSw = Stopwatch.StartNew();
        string token = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(100, 999)}";
        string rawPath = Path.Combine(AppContext.BaseDirectory, $"job_{token}_raw.bin");
        string inputPdf = Path.Combine(AppContext.BaseDirectory, $"job_{token}_input.pdf");
        string stampedPdf = Path.Combine(AppContext.BaseDirectory, $"job_{token}_stamped.pdf");

        long firstByteMs = 0, endMarkerMs = 0, receiveMs = 0, convertMs = 0, identityMs = 0, stampMs = 0, dispatchMs = 0;
#pragma warning disable CS0219
        bool earlyMarker = false;
#pragma warning restore CS0219
        string clientIp = "127.0.0.1";

        try
        {
            if (client.Client.RemoteEndPoint is IPEndPoint ep)
                clientIp = ep.Address.ToString();

            var rxSw = Stopwatch.StartNew();
            using (client)
            await using (var netStream = client.GetStream())
            await using (var fileStream = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                byte[] buffer = new byte[8192];
                int bytesRead;
                bool isFirst = true;

                while ((bytesRead = await netStream.ReadAsync(buffer)) > 0)
                {
                    if (isFirst) { firstByteMs = rxSw.ElapsedMilliseconds; isFirst = false; }
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));

                    string chunkText = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                    if (chunkText.Contains("%%EOF") || chunkText.Contains("%%Trailer"))
                    {
                        endMarkerMs = rxSw.ElapsedMilliseconds;
                        earlyMarker = true;
                    }
                }
            }
            rxSw.Stop();
            receiveMs = rxSw.ElapsedMilliseconds;
            if (endMarkerMs == 0) endMarkerMs = receiveMs;

            var fileInfo = new FileInfo(rawPath);
            if (!fileInfo.Exists || fileInfo.Length == 0) return;

            var convSw = Stopwatch.StartNew();
            if (IsFilePdf(rawPath))
                File.Copy(rawPath, inputPdf, overwrite: true);
            else
                ConvertPsToPdf(gsExePath, rawPath, inputPdf);
            convSw.Stop();
            convertMs = convSw.ElapsedMilliseconds;

            var idSw = Stopwatch.StartNew();
            var identity = await IdentityResolver.ResolveAsync(Environment.UserName, clientIp);
            idSw.Stop();
            identityMs = idSw.ElapsedMilliseconds;

            var stampSw = Stopwatch.StartNew();
            int jobId = Random.Shared.Next(100, 999);
            string userCodename = !string.IsNullOrWhiteSpace(identity.Codename) ? identity.Codename : identity.DisplayName;
            var meta = new StampMetadata(
                JobId: jobId,
                UserName: userCodename,
                ComputerName: identity.ComputerName,
                Timestamp: DateTimeOffset.UtcNow
            );

            WatermarkConfig wmConfig = await GetActiveWatermarkPolicyAsync();
            var (shortCode, hmacTag, pageCount) = WatermarkEngine.ApplyWatermark(inputPdf, stampedPdf, meta, wmConfig, clientIp);
            stampSw.Stop();
            stampMs = stampSw.ElapsedMilliseconds;

            // Keep an up-to-date inspection copy on the Desktop (if desktop directory accessible)
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (Directory.Exists(desktop))
                {
                    string desktopCopy = Path.Combine(desktop, "LastStampedJob.pdf");
                    File.Copy(stampedPdf, desktopCopy, overwrite: true);
                }
            }
            catch { }

            var dispSw = Stopwatch.StartNew();
            var queueLock = _queueLocks.GetOrAdd(backQueueName, _ => new SemaphoreSlim(1, 1));
            await queueLock.WaitAsync();
            try
            {
                await HybridPrinterDispatcher.DispatchStampedPdfAsync(
                    gsExePath, stampedPdf, backQueueName, printerIp, 9100, dispatchMode);
            }
            finally
            {
                queueLock.Release();
            }
            dispSw.Stop();
            dispatchMs = dispSw.ElapsedMilliseconds;

            totalSw.Stop();
            Console.WriteLine($"[√] Job #{meta.JobId} {meta.UserName}@{meta.ComputerName} ({pageCount}p, {shortCode}) printed in {totalSw.ElapsedMilliseconds}ms.");

            using var sha256 = SHA256.Create();
            using var fs = File.OpenRead(stampedPdf);
            string docHash = Convert.ToHexString(sha256.ComputeHash(fs));

            var record = new PrintAuditRecord
            {
                JobId = meta.JobId,
                KeyId = meta.KeyId,
                UserName = userCodename,
                ComputerName = identity.ComputerName,
                ClientIp = clientIp,
                Timestamp = meta.Timestamp,
                ShortCode = shortCode,
                HmacTag = hmacTag,
                DocumentSha256 = docHash,
                PageCount = pageCount
            };

            database.LogPrintJob(record);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, serverApiUrl);
                    req.Headers.Add("X-Api-Key", _apiKey);
                    req.Content = new StringContent(JsonSerializer.Serialize(record), Encoding.UTF8, "application/json");
                    await _http.SendAsync(req);
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Failed to process print stream: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(rawPath)) File.Delete(rawPath); } catch { }
            try { if (File.Exists(inputPdf)) File.Delete(inputPdf); } catch { }
            try { if (File.Exists(stampedPdf)) File.Delete(stampedPdf); } catch { }
        }
    }

    private static bool IsFilePdf(string path)
    {
        Span<byte> buffer = stackalloc byte[512];
        using var fs = File.OpenRead(path);
        int read = fs.Read(buffer);
        return Encoding.ASCII.GetString(buffer[..read]).Contains("%PDF-", StringComparison.Ordinal);
    }

    private static string DetectPrinterDriver(string printerName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT DriverName FROM Win32_Printer WHERE Name = '{printerName.Replace("'", "\\'")}'");
            foreach (ManagementObject obj in searcher.Get())
                return obj["DriverName"]?.ToString() ?? "Generic / Text Only";
        }
        catch { }
        return "Canon MF4500 Series UFRII LT";
    }

    private static (string Vendor, PrinterDispatchMode Mode) ResolveVendorAndMode(string driver)
    {
        string d = driver.ToLowerInvariant();
        if (d.Contains("canon") || d.Contains("ufrii") || d.Contains("gdi"))
            return ("Canon", PrinterDispatchMode.WindowsDriverQueue);
        if (d.Contains("hp") || d.Contains("laserjet") || d.Contains("pcl"))
            return ("HP/PCL6", PrinterDispatchMode.DirectPclSocket9100);
        if (d.Contains("postscript") || d.Contains("pdf"))
            return ("Direct-PDF", PrinterDispatchMode.DirectPdfSocket9100);
        return ("Generic", PrinterDispatchMode.AutoDetect);
    }

    private static void EnsurePrintServiceOperationalLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "wevtutil",
                Arguments = "sl Microsoft-Windows-PrintService/Operational /e:true",
                CreateNoWindow = true,
                UseShellExecute = false
            })?.WaitForExit(2000);
        }
        catch { }
    }

    private static long WarmUpEngines(string gsExe)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var proc = Process.Start(new ProcessStartInfo { FileName = gsExe, Arguments = "-v", CreateNoWindow = true, UseShellExecute = false });
            proc?.WaitForExit(1000);
        }
        catch { }
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    /// <summary>
    /// Resolves the Ghostscript executable path.
    /// Checks local embedded directory relative to AppContext.BaseDirectory first, then falls back to Program Files.
    /// </summary>
    private static string ResolveGhostscriptExecutable()
    {
        // 1. Embedded directory: <AppContext.BaseDirectory>\gs\gswin64c.exe
        string embeddedDirect = Path.Combine(AppContext.BaseDirectory, "gs", "gswin64c.exe");
        if (File.Exists(embeddedDirect)) return embeddedDirect;

        // 2. Embedded parent directory: <AppContext.BaseDirectory>\..\gs\gswin64c.exe (for dev/publish layouts)
        try
        {
            string embeddedParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "gs", "gswin64c.exe"));
            if (File.Exists(embeddedParent)) return embeddedParent;
        }
        catch { }

        // 3. System-wide Ghostscript in Program Files
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string gsRoot = Path.Combine(pf, "gs");
        if (Directory.Exists(gsRoot))
        {
            var exe = Directory.GetFiles(gsRoot, "gswin64c.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe != null) return exe;
        }

        // 4. Fallback to system PATH
        return "gswin64c.exe";
    }

    private static void ConvertPsToPdf(string gsExe, string psPath, string pdfPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gsExe,
            Arguments = $"-q -dBATCH -dNOPAUSE -dSAFER -dCompatibilityLevel=1.4 -dCompressObjects=false -dAutoRotatePages=/None -sDEVICE=pdfwrite -sOutputFile=\"{pdfPath}\" \"{psPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(15000);
    }
}