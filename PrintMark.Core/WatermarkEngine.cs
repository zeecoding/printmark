using System.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using QRCoder;

namespace PrintMark.Core;

public enum WatermarkPlacement
{
    DiagonalSingle = 0,     // Classic diagonal center (CONFIDENTIAL 1 / DRAFT 1)
    HorizontalCenter = 1,   // Flat center (CONFIDENTIAL 2 / DO NOT COPY 2 / DRAFT 2)
    TopCenter = 2,          // Upper page placement
    BottomCenter = 3,       // Lower page placement
    TiledGrid = 4           // Repeating diagonal pattern across entire page
}

public enum WatermarkContentMode
{
    TextOnly = 0,
    ImageOnly = 1,
    TextAndImage = 2
}

public class WatermarkConfig
{
    public bool Enabled { get; set; } = true;
    public WatermarkPlacement PrimaryPlacement { get; set; } = WatermarkPlacement.DiagonalSingle;
    public WatermarkContentMode ContentMode { get; set; } = WatermarkContentMode.TextOnly;

    public bool ShowTopBanner { get; set; } = true;
    public bool ShowBottomAuditFooter { get; set; } = true;
    public bool ShowQrCode { get; set; } = true;

    // Presets: "CONFIDENTIAL", "DO NOT COPY", "DRAFT", "SAMPLE", or "{UserName} | {ComputerName} | {DateTime}"
    public string TextTemplate { get; set; } = "{UserName} | {ComputerName} | {DateTime}";
    public string TopBannerText { get; set; } = "CONFIDENTIAL - INTERNAL DISTRIBUTION ONLY";

    // Image Watermark Options (Base64 PNG/JPG or local path)
    public string? ImageBase64 { get; set; }
    public string? ImagePath { get; set; }
    public double ImageWidthPt { get; set; } = 160.0;
    public double ImageHeightPt { get; set; } = 160.0;

    // Styling & Opacity
    public string FontFamily { get; set; } = "Arial";
    public double FontSize { get; set; } = 36.0;
    public double Opacity { get; set; } = 0.18;         // 0.05 to 1.0
    public double DirectionAngle { get; set; } = -45.0; // Angle for Diagonal & TiledGrid
    public string HexColor { get; set; } = "#DC2626";   // Color in Hex (#DC2626 red, #64748B slate)

    // Tiled Grid Spacing
    public double TileStepX { get; set; } = 190.0;
    public double TileStepY { get; set; } = 135.0;

    // Safe bottom margin in points (45pt = 1.58cm). Prevents GDI/Letter/hardware roller clipping.
    public double BottomMarginPt { get; set; } = 45.0;
}

public record StampMetadata(
    int JobId,
    string UserName,
    string ComputerName,
    DateTimeOffset Timestamp,
    string KeyId = "k1"
);

public static class WatermarkEngine
{
    public static (string ShortCode, string HmacTag, int PageCount) ApplyWatermark(
        string sourcePath,
        string targetPath,
        StampMetadata meta,
        WatermarkConfig? config = null,
        string clientIp = "127.0.0.1")
    {
        config ??= new WatermarkConfig();

        string rawPayload = $"{meta.KeyId}|{meta.JobId}|{meta.UserName}|{meta.ComputerName}|{clientIp}|{meta.Timestamp:O}";
        string hmacHex = StampCodes.ComputeTag(rawPayload);
        string timeHex = meta.Timestamp.ToUnixTimeSeconds().ToString("X8");
        string shortCode = $"{meta.KeyId}-{meta.JobId:X}-{timeHex}-{hmacHex}";
        string encryptedQrPayload = StampCodes.EncryptPayload(rawPayload);

        byte[] pdfBytes = File.ReadAllBytes(sourcePath);
        using var ms = new MemoryStream(pdfBytes);
        using PdfDocument document = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);

        int pageCount = document.PageCount;
        if (!config.Enabled)
        {
            document.Save(targetPath);
            return (shortCode, hmacHex, pageCount);
        }

        DateTimeOffset localDto = meta.Timestamp.ToLocalTime();
        string localTime = localDto.ToString("yyyy-MM-dd HH:mm:ss");
        string watermarkText = ResolveTemplate(config.TextTemplate, meta, localDto, clientIp, shortCode);

        byte[]? qrPngBytes = config.ShowQrCode ? GenerateQrCodeBytes(encryptedQrPayload) : null;
        byte[]? watermarkImgBytes = LoadWatermarkImageBytes(config);

        int alpha = Math.Clamp((int)(config.Opacity * 255), 8, 255);
        var (r, g, b) = ParseHexColor(config.HexColor);
        var mainBrush = new XSolidBrush(XColor.FromArgb(alpha, r, g, b));
        // Solid dark brush (alpha 240) so the bottom audit text and top banner never fade out
        var footerBrush = new XSolidBrush(XColor.FromArgb(240, 20, 20, 20));

        var mainFont = new XFont(config.FontFamily, config.FontSize, XFontStyle.Bold);
        var tileFont = new XFont(config.FontFamily, Math.Clamp(config.FontSize * 0.65, 14.0, 28.0), XFontStyle.Bold);
        var smallFont = new XFont(config.FontFamily, 8.0, XFontStyle.Bold);

        for (int i = 0; i < pageCount; i++)
        {
            PdfPage page = document.Pages[i];
            using XGraphics gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            // Use XGraphics.PageSize to accurately reflect Portrait vs Landscape canvas bounds
            double width = gfx.PageSize.Width;
            double height = gfx.PageSize.Height;
            bool isLandscape = width > height;

            // A. Top Security Banner
            if (config.ShowTopBanner && !string.IsNullOrWhiteSpace(config.TopBannerText))
            {
                gfx.DrawString(
                    $"{config.TopBannerText}  |  {meta.UserName}@{meta.ComputerName}  |  {localTime}",
                    smallFont,
                    footerBrush,
                    new XRect(0, 18, width, 16),
                    XStringFormats.TopCenter);
            }

            // B. Primary Watermark (Single Diagonal, Horizontal, Top, Bottom, or Tiled Grid + Image/Text)
            DrawPrimaryWatermark(gfx, config, watermarkText, watermarkImgBytes, mainFont, tileFont, mainBrush, width, height, isLandscape);

            // C. Bottom-Left Square QR Code & Audit Footer
            double safeBottomMargin = Math.Max(28.35, config.BottomMarginPt);
            double qrSize = 45.0; // Strict 45x45pt square
            double qrLeft = 36.0;
            double qrTop = height - safeBottomMargin - qrSize;

            if (qrPngBytes != null)
            {
                using var qrImage = XImage.FromStream(() => new MemoryStream(qrPngBytes));
                gfx.DrawImage(qrImage, qrLeft, qrTop, qrSize, qrSize);
            }

            if (config.ShowBottomAuditFooter)
            {
                string footer = $"ID: {shortCode} | User: {meta.UserName} | PC: {meta.ComputerName} | {localTime} | Page {i + 1}/{pageCount}";
                double textLeft = qrPngBytes != null ? (qrLeft + qrSize + 8.0) : 36.0;
                double textWidth = Math.Max(100.0, width - textLeft - 24.0);

                // Vertically center the audit line with the lower half of the QR code so it never gets clipped
                gfx.DrawString(
                    footer,
                    smallFont,
                    footerBrush,
                    new XRect(textLeft, qrTop + (qrSize / 2.0) - 6.0, textWidth, 14.0),
                    XStringFormats.TopLeft);
            }
        }

        document.Save(targetPath);
        return (shortCode, hmacHex, pageCount);
    }

    private static void DrawPrimaryWatermark(
        XGraphics gfx,
        WatermarkConfig config,
        string text,
        byte[]? imgBytes,
        XFont mainFont,
        XFont tileFont,
        XBrush brush,
        double width,
        double height,
        bool isLandscape)
    {
        double angle = config.PrimaryPlacement == WatermarkPlacement.HorizontalCenter
            ? 0.0
            : (config.DirectionAngle == 0 ? (isLandscape ? -28.0 : -45.0) : config.DirectionAngle);

        if (config.PrimaryPlacement == WatermarkPlacement.TiledGrid)
        {
            double stepX = Math.Max(90.0, config.TileStepX);
            double stepY = Math.Max(70.0, config.TileStepY);

            for (double y = 40.0; y < height - 60.0; y += stepY)
            {
                int rowIdx = (int)(y / stepY);
                double offsetX = (rowIdx % 2 == 0) ? 0.0 : (stepX / 2.0);

                for (double x = 40.0; x < width - 40.0; x += stepX)
                {
                    var tileState = gfx.Save();
                    gfx.TranslateTransform(x + offsetX, y);
                    gfx.RotateTransform(angle);

                    if (imgBytes != null && config.ContentMode != WatermarkContentMode.TextOnly)
                    {
                        using var tileImg = XImage.FromStream(() => new MemoryStream(imgBytes));
                        gfx.DrawImage(tileImg, -25, -25, 50, 50);
                    }

                    if (config.ContentMode != WatermarkContentMode.ImageOnly && !string.IsNullOrWhiteSpace(text))
                    {
                        gfx.DrawString(text, tileFont, brush, new XPoint(0, 0), XStringFormats.Center);
                    }

                    gfx.Restore(tileState);
                }
            }
            return;
        }

        double centerY = config.PrimaryPlacement switch
        {
            WatermarkPlacement.TopCenter => height * 0.20,
            WatermarkPlacement.BottomCenter => height * 0.78,
            _ => height / 2.0
        };

        var state = gfx.Save();
        gfx.TranslateTransform(width / 2.0, centerY);
        gfx.RotateTransform(angle);

        if (imgBytes != null && (config.ContentMode == WatermarkContentMode.ImageOnly || config.ContentMode == WatermarkContentMode.TextAndImage))
        {
            using var wmImage = XImage.FromStream(() => new MemoryStream(imgBytes));
            double w = config.ImageWidthPt;
            double h = config.ImageHeightPt;
            // Apply opacity overlay or draw centered
            gfx.DrawImage(wmImage, -w / 2.0, -h / 2.0, w, h);
        }

        if (config.ContentMode != WatermarkContentMode.ImageOnly && !string.IsNullOrWhiteSpace(text))
        {
            double textY = (imgBytes != null && config.ContentMode == WatermarkContentMode.TextAndImage)
                ? (config.ImageHeightPt / 2.0 + 24.0)
                : 0.0;
            gfx.DrawString(text, mainFont, brush, new XPoint(0, textY), XStringFormats.Center);
        }

        gfx.Restore(state);
    }

    private static string ResolveTemplate(string template, StampMetadata meta, DateTimeOffset localDto, string clientIp, string shortCode)
    {
        return template
            .Replace("{UserName}", meta.UserName, StringComparison.OrdinalIgnoreCase)
            .Replace("{ComputerName}", meta.ComputerName, StringComparison.OrdinalIgnoreCase)
            .Replace("{DateTime}", localDto.ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{Date}", localDto.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{Time}", localDto.ToString("HH:mm:ss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{IpAddress}", clientIp, StringComparison.OrdinalIgnoreCase)
            .Replace("{JobId}", meta.JobId.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{ShortCode}", shortCode, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? LoadWatermarkImageBytes(WatermarkConfig config)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(config.ImageBase64))
            {
                string clean = config.ImageBase64.Contains(',')
                    ? config.ImageBase64[(config.ImageBase64.IndexOf(',') + 1)..]
                    : config.ImageBase64;
                return Convert.FromBase64String(clean);
            }
            if (!string.IsNullOrWhiteSpace(config.ImagePath) && File.Exists(config.ImagePath))
            {
                return File.ReadAllBytes(config.ImagePath);
            }
        }
        catch { }
        return null;
    }

    private static byte[] GenerateQrCodeBytes(string content)
    {
        using var qrGenerator = new QRCodeGenerator();
        using QRCodeData qrCodeData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrCodeData);
        return qrCode.GetGraphic(4);
    }

    private static (int R, int G, int B) ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6 &&
            int.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out int r) &&
            int.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out int g) &&
            int.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out int b))
        {
            return (r, g, b);
        }
        return (220, 38, 38);
    }
}