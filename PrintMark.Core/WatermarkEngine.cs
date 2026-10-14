using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PrintMark.Data;
using QRCoder;

namespace PrintMark.Core;

public static class WatermarkEngine
{
    private static readonly byte[] HmacSecretKey = Encoding.UTF8.GetBytes("LAB_PRINTMARK_SIGNING_SECRET_2026");

    public static void ApplyWatermark(string sourcePath, string targetPath, StampMetadata meta, string? dbPath = null)
    {
        // 1. Calculate HMAC-SHA256 signature
        string canonical = $"{meta.KeyId}:{meta.JobId}:{meta.UserName}:{meta.ComputerName}:{meta.Timestamp.ToUnixTimeSeconds()}";
        using var hmac = new HMACSHA256(HmacSecretKey);
        string hmacTag = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)))[..16];
        string shortCode = $"{meta.KeyId}-{meta.JobId:X}-{meta.Timestamp.ToUnixTimeSeconds():X}-{hmacTag}";

        // 2. Open input PDF in modify mode
        using var document = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify);

        var fontWatermark = new XFont("Arial", 22, XFontStyle.Bold);
        var fontHeader = new XFont("Arial", 9, XFontStyle.Regular);
        var fontFooter = new XFont("Courier New", 8, XFontStyle.Regular);

        var watermarkBrush = new XSolidBrush(XColor.FromArgb(45, 0, 0, 0));
        var headerBrush = new XSolidBrush(XColor.FromArgb(140, 0, 0, 0));
        var footerBrush = new XSolidBrush(XColor.FromArgb(180, 0, 0, 0));

        // Generate QR code bitmap
        string qrPayload = $"V1|{shortCode}|{meta.UserName}|{meta.ComputerName}";
        byte[] qrPngBytes = GenerateQrPng(qrPayload);
        var qrImage = XImage.FromStream(() => new MemoryStream(qrPngBytes));

        foreach (var page in document.Pages)
        {
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            double width = page.Width.Point;
            double height = page.Height.Point;

            // Top Header
            gfx.DrawString(
                "CONFIDENTIAL - INTERNAL DISTRIBUTION ONLY",
                fontHeader,
                headerBrush,
                new XRect(0, 25, width, 20),
                XStringFormats.TopCenter
            );

            // Diagonal Center Watermark
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

            // Bottom Left QR Code
            double qrSize = 50;
            double margin = 30;
            gfx.DrawImage(qrImage, margin, height - margin - qrSize, qrSize, qrSize);

            // Bottom Audit Footer
            string auditText = $"{shortCode} | Job {meta.JobId} | User: {meta.UserName} | Host: {meta.ComputerName}";
            gfx.DrawString(
                auditText,
                fontFooter,
                footerBrush,
                new XPoint(margin + qrSize + 10, height - margin - 15)
            );
        }

        document.Save(targetPath);

        // Optional database logging
        if (!string.IsNullOrEmpty(dbPath))
        {
            using var sha256 = SHA256.Create();
            byte[] fileHashBytes;
            using (var fs = File.OpenRead(targetPath))
            {
                fileHashBytes = sha256.ComputeHash(fs);
            }

            var db = new AuditDatabase(dbPath);
            db.LogPrintJob(new PrintAuditRecord
            {
                JobId = meta.JobId,
                KeyId = meta.KeyId,
                UserName = meta.UserName,
                ComputerName = meta.ComputerName,
                Timestamp = meta.Timestamp,
                ShortCode = shortCode,
                HmacTag = hmacTag,
                DocumentSha256 = Convert.ToHexString(fileHashBytes),
                PageCount = document.PageCount
            });
        }
    }

    private static byte[] GenerateQrPng(string payload)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrData);
        return qrCode.GetGraphic(20);
    }
}