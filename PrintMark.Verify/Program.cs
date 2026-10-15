using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PrintMark.Data;

namespace PrintMark.Verify;

public class Program
{
    private static readonly byte[] HmacSecretKey = Encoding.UTF8.GetBytes("LAB_PRINTMARK_SIGNING_SECRET_2026");

    public static void Main(string[] args)
    {
        Console.WriteLine("=== PrintMark Forensic Audit Verifier ===");

        string dbPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "printmark_audit.db");

        if (!File.Exists(dbPath))
        {
            // Fallback to local working directory if running via dotnet run
            dbPath = "printmark_audit.db";
        }

        Console.Write("Enter printed ShortCode (e.g., k1-412-670E1A2B-A1B2C3D4E5F60718): ");
        string? shortCode = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(shortCode))
        {
            Console.WriteLine("[!] ShortCode cannot be empty.");
            return;
        }

        var db = new AuditDatabase(dbPath);
        PrintAuditRecord? record = db.FindByShortCode(shortCode);

        if (record == null)
        {
            Console.WriteLine($"[✗] No audit record found in '{dbPath}' for ShortCode: {shortCode}");
            return;
        }

        // Recompute HMAC-SHA256 to verify cryptographic integrity
        long unixTime = record.Timestamp.ToUnixTimeSeconds();
        string canonical = $"{record.KeyId}:{record.JobId}:{record.UserName}:{record.ComputerName}:{unixTime}";
        using var hmac = new HMACSHA256(HmacSecretKey);
        string expectedTag = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)))[..16];

        bool isSignatureValid = string.Equals(expectedTag, record.HmacTag, StringComparison.OrdinalIgnoreCase);

        Console.WriteLine();
        Console.WriteLine(isSignatureValid
            ? "[✓] CRYPTOGRAPHIC SIGNATURE VERIFIED (Authentic PrintMark Record)"
            : "[!] WARNING: SIGNATURE MISMATCH (Record may have been tampered with!)");
        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine($"Audit ID       : {record.Id}");
        Console.WriteLine($"Job ID         : {record.JobId}");
        Console.WriteLine($"Short Code     : {record.ShortCode}");
        Console.WriteLine($"HMAC Tag       : {record.HmacTag} (Expected: {expectedTag})");
        Console.WriteLine($"Timestamp (UTC): {record.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"User Name      : {record.UserName}");
        Console.WriteLine($"User SID       : {record.UserSid}");
        Console.WriteLine($"User GUID      : {record.UserGuid}");
        Console.WriteLine($"Computer Name  : {record.ComputerName}");
        Console.WriteLine($"Machine SID    : {record.MachineSid}");
        Console.WriteLine($"Machine GUID   : {record.MachineGuid}");
        Console.WriteLine($"Client IP      : {record.ClientIp}");
        Console.WriteLine($"Client MAC     : {record.ClientMac}");
        Console.WriteLine($"Auth Type      : {record.AuthType}");
        Console.WriteLine($"Page Count     : {record.PageCount}");
        Console.WriteLine($"PDF SHA-256    : {record.DocumentSha256}");
        Console.WriteLine("------------------------------------------------------------");
    }
}