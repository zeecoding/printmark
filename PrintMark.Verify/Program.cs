using System;
using System.Security.Cryptography;
using System.Text;

namespace PrintMark.Verify;

public class Program
{
    // Secret signing key matching PrintMark.Core
    private static readonly byte[] HmacSecretKey = Encoding.UTF8.GetBytes("LAB_PRINTMARK_SIGNING_SECRET_2026");

    public static void Main(string[] args)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("       PrintMark Document Authenticity Verifier   ");
        Console.WriteLine("==================================================\n");

        string input = args.Length > 0 ? args[0] : "";
        if (string.IsNullOrWhiteSpace(input))
        {
            Console.Write("Enter QR Payload or Audit Short-Code: ");
            input = Console.ReadLine()?.Trim() ?? string.Empty;
        }

        VerifyAuditCode(input);
    }

    public static void VerifyAuditCode(string rawInput)
    {
        try
        {
            string shortCode;
            string userName = "UNKNOWN";
            string computerName = "UNKNOWN";

            // Format 1: Scanned QR string (V1|ShortCode|User|Host)
            if (rawInput.StartsWith("V1|"))
            {
                var parts = rawInput.Split('|');
                if (parts.Length < 4)
                {
                    Console.WriteLine("[✗] Malformed QR payload structure.");
                    return;
                }
                shortCode = parts[1];
                userName = parts[2];
                computerName = parts[3];
            }
            // Format 2: Direct Audit Short-Code from footer
            else
            {
                shortCode = rawInput;
            }

            // Parse: KeyId-JobHex-TimeHex-HmacTag
            var tokens = shortCode.Split('-');
            if (tokens.Length != 4)
            {
                Console.WriteLine("[✗] Invalid short-code structure. Expected format: KeyId-JobHex-TimeHex-HmacTag.");
                return;
            }

            string keyId = tokens[0];
            int jobId = Convert.ToInt32(tokens[1], 16);
            long unixSeconds = Convert.ToInt64(tokens[2], 16);
            string presentedHmac = tokens[3];

            DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

            // Re-derive canonical payload
            string canonical = $"{keyId}:{jobId}:{userName}:{computerName}:{unixSeconds}";
            using var hmac = new HMACSHA256(HmacSecretKey);
            string expectedHmac = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)))[..16];

            // Constant-time comparison to prevent timing attacks
            bool isValid = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presentedHmac),
                Encoding.UTF8.GetBytes(expectedHmac)
            );

            Console.WriteLine("---------------- Database Audit Record ----------------");
            Console.WriteLine($"Database ID      : #{record.Id}");
            Console.WriteLine($"Job ID           : {record.JobId}");
            Console.WriteLine($"User Account     : {record.UserName}");
            Console.WriteLine($"Workstation Host : {record.ComputerName}");
            Console.WriteLine($"Client IP        : {record.ClientIp}");
            Console.WriteLine($"Client MAC       : {record.ClientMac}");
            Console.WriteLine($"User AD SID      : {record.UserSid}");
            Console.WriteLine($"User AD GUID     : {record.UserGuid}");
            Console.WriteLine($"Machine AD SID   : {record.MachineSid}");
            Console.WriteLine($"Machine AD GUID  : {record.MachineGuid}");
            Console.WriteLine($"Auth Protocol    : {record.AuthType}");
            Console.WriteLine($"Print Timestamp  : {record.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
            Console.WriteLine($"Document SHA256  : {record.DocumentSha256}");
            Console.WriteLine($"Stored HMAC Tag  : {record.HmacTag}");
            Console.WriteLine("-------------------------------------------------------");

            if (isValid)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[✓] AUTHENTIC: Document cryptographic signature verified.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[✗] TAMPER DETECTED: Invalid signature. Document may be forged.");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Error: {ex.Message}");
        }
    }
}