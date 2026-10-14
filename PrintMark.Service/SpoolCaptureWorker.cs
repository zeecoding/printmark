using System.Net;
using System.Net.Sockets;
using PrintMark.Core;
using PrintMark.Data;

namespace PrintMark.Service;

public class SpoolCaptureWorker : BackgroundService
{
    private readonly ILogger<SpoolCaptureWorker> _logger;
    private readonly string _spoolDir;
    private readonly AuditDatabase _database;
    private TcpListener? _listener;
    private const int ListenPort = 9101;

    public SpoolCaptureWorker(ILogger<SpoolCaptureWorker> logger)
    {
        _logger = logger;
        _spoolDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SpoolQueue");
        Directory.CreateDirectory(_spoolDir);
        _database = new AuditDatabase(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "printmark_audit.db"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.Loopback, ListenPort);
        _listener.Start();
        _logger.LogInformation("PrintMark Spool Capture Listener active on 127.0.0.1:{Port}", ListenPort);

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
        string jobId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        string rawJobPath = Path.Combine(_spoolDir, $"job_{jobId}_raw.bin");
        string stampedJobPath = Path.Combine(_spoolDir, $"job_{jobId}_stamped.pdf");

        // 1. Extract physical network endpoint IP
        string clientIp = "127.0.0.1";
        if (client.Client.RemoteEndPoint is IPEndPoint remoteEndPoint)
        {
            clientIp = remoteEndPoint.Address.ToString();
        }

        _logger.LogInformation("Capturing print job {JobId} incoming from IP: {ClientIp}", jobId, clientIp);

        try
        {
            using (client)
            await using (var networkStream = client.GetStream())
            await using (var fileStream = File.Create(rawJobPath))
            {
                await networkStream.CopyToAsync(fileStream, ct);
            }

            _logger.LogInformation("Raw job {JobId} saved ({Size} bytes). Resolving identities...", jobId, new FileInfo(rawJobPath).Length);

            string userName = Environment.UserName;
            string computerName = Environment.MachineName;

            // 2. Resolve MAC Address + AD User SID + AD Machine SID
            var identity = IdentityResolver.ResolveContext(clientIp, userName, computerName);
            _logger.LogInformation("Resolved identity: UserSID={UserSid}, MachineSID={MachineSid}, MAC={Mac}", 
                identity.UserSid, identity.MachineSid, identity.ClientMac);

            var metadata = new StampMetadata(
                JobId: (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000),
                UserName: userName,
                ComputerName: computerName,
                Timestamp: DateTimeOffset.UtcNow
            );

            // 3. Stamp document
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "printmark_audit.db");
            WatermarkEngine.ApplyWatermark(rawJobPath, stampedJobPath, metadata);

            // 4. Compute file SHA256 and store forensic log in database
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] fileHashBytes;
            using (var fs = File.OpenRead(stampedJobPath))
            {
                fileHashBytes = sha256.ComputeHash(fs);
            }

            // Recompute short-code tag for audit tracking
            string canonical = $"{metadata.KeyId}:{metadata.JobId}:{metadata.UserName}:{metadata.ComputerName}:{metadata.Timestamp.ToUnixTimeSeconds()}";
            using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes("LAB_PRINTMARK_SIGNING_SECRET_2026"));
            string hmacTag = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical)))[..16];
            string shortCode = $"{metadata.KeyId}-{metadata.JobId:X}-{metadata.Timestamp.ToUnixTimeSeconds():X}-{hmacTag}";

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
                PageCount = 1
            });

            _logger.LogInformation("[✓] Job {JobId} successfully stamped and audited.", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process print job {JobId}", jobId);
        }
    }

    public override void Dispose()
    {
        _listener?.Stop();
        base.Dispose();
    }
}