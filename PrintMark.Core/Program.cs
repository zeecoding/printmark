using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using QRCoder;
using PrintMark.Data;
namespace PrintMark.Core;

public record StampMetadata(
    int JobId,
    string UserName,
    string ComputerName,
    DateTimeOffset Timestamp,
    string KeyId = "k1"
);

public class Program
{
    private static readonly byte[] HmacSecretKey = Encoding.UTF8.GetBytes("LAB_PRINTMARK_SIGNING_SECRET_2026");

    public static void Main(string[] args)
    {
        string inputPath = "sample_input.pdf";
        string outputPath = "stamped_output.pdf";

        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"[!] '{inputPath}' not found.");
            Console.WriteLine("[*] Please print any document using 'Microsoft Print to PDF' and save it as 'sample_input.pdf' in this folder.");
            return;
        }

        var metadata = new StampMetadata(
            JobId: 1042,
            UserName: "m.zubair",
            ComputerName: "SURFACE-LAPTOP-5",
            Timestamp: DateTimeOffset.UtcNow
        );

        Console.WriteLine($"[*] Stamping document: {inputPath} -> {outputPath}");
        ApplyWatermark(inputPath, outputPath, metadata);
        Console.WriteLine("[✓] Stamping completed successfully!");
    }

    public static void ApplyWatermark(string sourcePath, string targetPath, StampMetadata meta)
    {
        // 1. Calculate HMAC-SHA256 signature
        string canonical = $"{meta.KeyId}:{meta.JobId}:{meta.UserName}:{meta.ComputerName}:{meta.Timestamp.ToUnixTimeSeconds()}";
        using var hmac = new HMACSHA256(HmacSecretKey);
        string hmacTag = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)))[..16];
        string shortCode = $"{meta.KeyId}-{meta.JobId:X}-{meta.Timestamp.ToUnixTimeSeconds():X}-{hmacTag}";

        // 2. Open input PDF in modify mode
        using var document = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify);

        // Standard typography
        var fontWatermark = new XFont("Arial", 22, XFontStyle.Bold);
        var fontHeader = new XFont("Arial", 9, XFontStyle.Regular);
        var fontFooter = new XFont("Courier New", 8, XFontStyle.Regular);

        // Brushes
        var watermarkBrush = new XSolidBrush(XColor.FromArgb(45, 0, 0, 0)); // ~18% opacity
        var headerBrush = new XSolidBrush(XColor.FromArgb(140, 0, 0, 0));
        var footerBrush = new XSolidBrush(XColor.FromArgb(180, 0, 0, 0));

        // Generate QR code bitmap
        string qrPayload = $"V1|{shortCode}|{meta.UserName}|{meta.ComputerName}";
        byte[] qrPngBytes = GenerateQrPng(qrPayload);
        using var qrMemoryStream = new MemoryStream(qrPngBytes);
        var qrImage = XImage.FromStream(() => new MemoryStream(qrPngBytes));

        foreach (var page in document.Pages)
        {
            // Use XGraphicsPdfPageOptions.Append to overlay on top of existing content
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            double width = page.Width.Point;
            double height = page.Height.Point;

            // --- Top Header ---
            gfx.DrawString(
                "CONFIDENTIAL - INTERNAL DISTRIBUTION ONLY",
                fontHeader,
                headerBrush,
                new XRect(0, 25, width, 20),
                XStringFormats.TopCenter
            );

            // --- Diagonal Center Watermark ---
            gfx.Save();
            gfx.TranslateTransform(width / 2.0, height / 2.0);
            gfx.RotateTransform(-35);
            string watermarkText = $"{meta.UserName} • {meta.ComputerName} • {meta.Timestamp:yyyy-MM-dd HH:mm:ss} UTC";
            gfx.DrawString(
                watermarkText,
                fontWatermark,
                watermarkBrush,
                new XPoint(-gfx.MeasureString(watermarkText, fontWatermark).Width / 2.0, 0)
            );
            gfx.Restore();

            // --- Bottom Left Vector/PNG QR Code ---
            double qrSize = 50;
            double margin = 30;
            gfx.DrawImage(qrImage, margin, height - margin - qrSize, qrSize, qrSize);

            // --- Bottom Audit Footer ---
            string auditText = $"{shortCode} | Job {meta.JobId} | User: {meta.UserName} | Host: {meta.ComputerName}";
            gfx.DrawString(
                auditText,
                fontFooter,
                footerBrush,
                new XPoint(margin + qrSize + 10, height - margin - 15)
            );
        }
            // Compute SHA256 of the output file
    using var sha256 = SHA256.Create();
    byte[] fileHashBytes;
    using (var fs = File.OpenRead(targetPath))
    {
        fileHashBytes = sha256.ComputeHash(fs);
    }
    string docHash = Convert.ToHexString(fileHashBytes);

    // Save to SQLite
    var db = new AuditDatabase("printmark_audit.db");
    db.LogPrintJob(new PrintAuditRecord
    {
        JobId = meta.JobId,
        KeyId = meta.KeyId,
        UserName = meta.UserName,
        ComputerName = meta.ComputerName,
        Timestamp = meta.Timestamp,
        ShortCode = shortCode,
        HmacTag = hmacTag,
        DocumentSha256 = docHash,
        PageCount = document.PageCount
    });
        document.Save(targetPath);
    }

    private static byte[] GenerateQrPng(string payload)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrData);
        return qrCode.GetGraphic(20);
    }
}