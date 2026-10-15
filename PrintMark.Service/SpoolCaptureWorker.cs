using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using PrintMark.Core;
using PrintMark.Data;

namespace PrintMark.Service;

public class SpoolCaptureWorker : BackgroundService
{
    private readonly ILogger<SpoolCaptureWorker> _logger;
    private readonly IConfiguration _config;
    private readonly string _spoolDir;
    private readonly AuditDatabase _database;
    private TcpListener? _listener;

    public SpoolCaptureWorker(ILogger<SpoolCaptureWorker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
        _spoolDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SpoolQueue");
        Directory.CreateDirectory(_spoolDir);
        _database = new AuditDatabase(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "printmark_audit.db"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int listenPort = _config.GetValue<int>("PrintMark:ListenPort", 9101);
        _listener = new TcpListener(IPAddress.Any, listenPort);
        _listener.Start();
        _logger.LogInformation("PrintMark Spool Capture Listener active on 0.0.0.0:{Port}", listenPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => ProcessIncomingJobAsync(client, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting client spool connection.");
            }
        }
    }

    private async Task ProcessIncomingJobAsync(TcpClient client, CancellationToken ct)
    {
        string fileToken = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        string rawJobPath = Path.Combine(_spoolDir, $"job_{fileToken}_raw.bin");
        string inputPdfPath = Path.Combine(_spoolDir, $"job_{fileToken}_input.pdf");
        string stampedJobPath = Path.Combine(_spoolDir, $"job_{fileToken}_stamped.pdf");

        string frontQueueName = _config.GetValue<string>("PrintMark:FrontQueueName") ?? "Canon-Protected";
        string backQueueName = _config.GetValue<string>("PrintMark:BackQueueName") ?? "MF4500 Series";

        string socketIp = "127.0.0.1";
        if (client.Client.RemoteEndPoint is IPEndPoint remoteEndPoint)
        {
            socketIp = remoteEndPoint.Address.ToString();
        }

        SpoolJobInfo spoolJob;

        try
        {
            using (client)
            await using (var networkStream = client.GetStream())
            await using (var fileStream = File.Create(rawJobPath))
            {
                // CRITICAL: Query Windows Spooler BEFORE closing the TCP connection so the job is still active!
                spoolJob = IdentityResolver.ResolveActiveSpoolJob(frontQueueName);
                _logger.LogInformation(
                    "Capturing Spooler Job #{JobId} ('{Doc}') from User={User}, Host={Host}",
                    spoolJob.JobId, spoolJob.DocumentName, spoolJob.UserName, spoolJob.ComputerName);

                await networkStream.CopyToAsync(fileStream, ct);
            }

            long rawSize = new FileInfo(rawJobPath).Length;
            if (rawSize == 0)
            {
                _logger.LogWarning("Captured 0-byte stream for Job #{JobId}; skipping.", spoolJob.JobId);
                return;
            }

            // 1. Resolve Real Client LAN IP, MAC Address, AD User SID, and AD Machine SID
            var identity = IdentityResolver.ResolveContext(socketIp, spoolJob.UserName, spoolJob.ComputerName);
            _logger.LogInformation(
                "Resolved Client Identity: IP={Ip}, MAC={Mac}, UserSID={UserSid}, MachineSID={MachineSid}",
                identity.ClientIp, identity.ClientMac, identity.UserSid, identity.MachineSid);

            // 2. Ensure we have a valid PDF for WatermarkEngine (convert PostScript -> PDF if needed)
            if (IsFilePdf(rawJobPath))
            {
                File.Copy(rawJobPath, inputPdfPath, overwrite: true);
            }
            else
            {
                _logger.LogInformation("Converting captured PostScript stream to PDF via Ghostscript...");
                ConvertPostScriptToPdf(rawJobPath, inputPdfPath);
            }

            // 3. Stamp PDF with WatermarkEngine
            var metadata = new StampMetadata(
                JobId: spoolJob.JobId,
                UserName: spoolJob.UserName,
                ComputerName: spoolJob.ComputerName,
                Timestamp: DateTimeOffset.UtcNow
            );

            var (shortCode, hmacTag, pageCount) = WatermarkEngine.ApplyWatermark(inputPdfPath, stampedJobPath, metadata);

            // 4. Compute SHA-256 of stamped PDF and log full forensic record to SQLite
            using var sha256 = SHA256.Create();
            byte[] fileHashBytes;
            using (var fs = File.OpenRead(stampedJobPath))
            {
                fileHashBytes = sha256.ComputeHash(fs);
            }

            _database.LogPrintJob(new PrintAuditRecord
            {
                JobId = metadata.JobId,
                KeyId = metadata.KeyId,
                UserName = metadata.UserName,
                ComputerName = metadata.ComputerName,
                ClientIp = identity.ClientIp,
                ClientMac = identity.ClientMac,
                UserSid = identity.UserSid,
                UserGuid = identity.UserGuid,
                MachineSid = identity.MachineSid,
                MachineGuid = identity.MachineGuid,
                AuthType = identity.AuthType,
                Timestamp = metadata.Timestamp,
                ShortCode = shortCode,
                HmacTag = hmacTag,
                DocumentSha256 = Convert.ToHexString(fileHashBytes),
                PageCount = pageCount
            });

            _logger.LogInformation("[✓] Stamped & audited Job #{JobId} ({Pages} pages, Code={Code}). Forwarding to '{BackQueue}'...",
                metadata.JobId, pageCount, shortCode, backQueueName);

            // 5. Forward Stamped PDF to the physical Canon printer queue (MF4500 Series)
            PrintPdfToWindowsQueue(stampedJobPath, backQueueName);

            _logger.LogInformation("[✓] Job #{JobId} dispatched to physical printer '{BackQueue}'.", metadata.JobId, backQueueName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process print job {Token}", fileToken);
        }
    }

    private static bool IsFilePdf(string filePath)
    {
        byte[] buffer = new byte[1024];
        using var fs = File.OpenRead(filePath);
        int read = fs.Read(buffer, 0, buffer.Length);
        string header = Encoding.ASCII.GetString(buffer, 0, read);
        return header.Contains("%PDF-", StringComparison.Ordinal);
    }

    private string ResolveGhostscriptExecutable()
    {
        string? configured = _config.GetValue<string>("PrintMark:GhostscriptPath");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string gsRoot = Path.Combine(programFiles, "gs");
        if (Directory.Exists(gsRoot))
        {
            var exe = Directory.GetFiles(gsRoot, "gswin64c.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe != null) return exe;
        }

        return "gswin64c.exe"; // Assumes in system PATH
    }

    private void ConvertPostScriptToPdf(string psFilePath, string outputPdfPath)
    {
        string gsExe = ResolveGhostscriptExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = gsExe,
            Arguments = $"-dBATCH -dNOPAUSE -dSAFER -sDEVICE=pdfwrite -sOutputFile=\"{outputPdfPath}\" \"{psFilePath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Ghostscript.");
        string stdErr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30000);

        if (!File.Exists(outputPdfPath) || new FileInfo(outputPdfPath).Length == 0)
        {
            throw new InvalidOperationException($"Ghostscript failed to convert spool file to PDF. ExitCode={proc.ExitCode}, Error={stdErr}");
        }
    }

    private void PrintPdfToWindowsQueue(string pdfFilePath, string backQueueName)
    {
        string gsExe = ResolveGhostscriptExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = gsExe,
            Arguments = $"-dBATCH -dNOPAUSE -dNOSAFER -dPrinted -sDEVICE=mswinpr2 -sOutputFile=\"%printer%{backQueueName}\" \"{pdfFilePath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Ghostscript for physical printing.");
        string stdErr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60000);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"Ghostscript failed printing to '{backQueueName}'. ExitCode={proc.ExitCode}, Error={stdErr}");
        }
    }

    public override void Dispose()
    {
        _listener?.Stop();
        base.Dispose();
    }
}