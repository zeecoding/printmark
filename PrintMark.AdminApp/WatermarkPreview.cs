using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrintMark.Core;
using QRCoder;

namespace PrintMark.AdminApp;

// Point-based sample canvas follows the placement calculations in WatermarkEngine.
// PDF export remains the authority for final print rendering and font metrics.
public sealed class WatermarkPreview : FrameworkElement
{
    public WatermarkConfig Config { get; set; } = new();
    public static StampMetadata SampleMetadata { get; } = new(412, "sample.user", "SAMPLE-PC", DateTimeOffset.Now);
    private string? _imageKey;
    private ImageSource? _image;
    private ImageSource? _qr;

    public static BitmapImage DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
    private static FormattedText Text(string text, double size, Brush brush, string font = "Arial", bool bold = false)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(font), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), size, brush, 1);
    private static void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush, bool bold = false)
        => dc.DrawText(Text(text, size, brush, bold: bold), new Point(x, y));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth; var h = ActualHeight;
        var cfg = Config;
        dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(218, 224, 232)), 1), new Rect(0, 0, w, h));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        DrawText(dc, "PRINTMARK / SAMPLE DOCUMENT", 48, 58, 10, Brushes.SlateGray, true);
        DrawText(dc, "Financial summary", 48, 96, 23, Brushes.Black, true);
        DrawText(dc, "Sample content for reviewing your watermark policy.", 48, 138, 11, Brushes.Gray);
        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(224, 229, 235)), 1);
        for (var i = 0; i < 14; i++) dc.DrawLine(linePen, new Point(48, 190 + i * 24), new Point(w - 48, 190 + i * 24));
        if (!cfg.Enabled) { dc.Pop(); return; }
        var meta = SampleMetadata;
        var time = meta.Timestamp.ToLocalTime();
        var raw = $"{meta.KeyId}|{meta.JobId}|{meta.UserName}|{meta.ComputerName}|127.0.0.1|{meta.Timestamp:O}";
        var code = $"{meta.KeyId}-{meta.JobId:X}-{meta.Timestamp.ToUnixTimeSeconds():X8}-{StampCodes.ComputeTag(raw)}";
        var text = cfg.TextTemplate;
        foreach (var pair in new Dictionary<string, string> {
            ["{UserName}"] = meta.UserName, ["{ComputerName}"] = meta.ComputerName,
            ["{DateTime}"] = time.ToString("yyyy-MM-dd HH:mm:ss"), ["{Date}"] = time.ToString("yyyy-MM-dd"),
            ["{Time}"] = time.ToString("HH:mm:ss"), ["{IpAddress}"] = "127.0.0.1", ["{JobId}"] = "412", ["{ShortCode}"] = code })
            text = text.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        if (cfg.ShowTopBanner)
        {
            var banner = Text($"{cfg.TopBannerText}  |  {meta.UserName}@{meta.ComputerName}  |  {time:yyyy-MM-dd HH:mm:ss}", 8, Brushes.Black, cfg.FontFamily, true);
            dc.DrawText(banner, new Point((w - banner.Width) / 2, 18));
        }
        var imageKey = cfg.ImageBase64 ?? cfg.ImagePath;
        if (_imageKey != imageKey)
        {
            _imageKey = imageKey; _image = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(cfg.ImageBase64))
                    _image = DecodeImage(Convert.FromBase64String(cfg.ImageBase64[(cfg.ImageBase64.IndexOf(',') + 1)..]));
                else if (!string.IsNullOrWhiteSpace(cfg.ImagePath) && File.Exists(cfg.ImagePath)) _image = DecodeImage(File.ReadAllBytes(cfg.ImagePath));
            }
            catch { /* Invalid stored image is omitted, matching the print engine. */ }
        }
        var angle = cfg.PrimaryPlacement == WatermarkPlacement.HorizontalCenter ? 0 : cfg.DirectionAngle == 0 ? (w > h ? -28 : -45) : cfg.DirectionAngle;
        var color = (Color)ColorConverter.ConvertFromString(cfg.HexColor);
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((int)(cfg.Opacity * 255), 8, 255), color.R, color.G, color.B));
        void Stamp(double x, double y, bool tile)
        {
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new RotateTransform(angle));
            if (_image != null && cfg.ContentMode != WatermarkContentMode.TextOnly)
            {
                var iw = tile ? 50 : cfg.ImageWidthPt; var ih = tile ? 50 : cfg.ImageHeightPt;
                dc.DrawImage(_image, new Rect(-iw / 2, -ih / 2, iw, ih));
            }
            if (cfg.ContentMode != WatermarkContentMode.ImageOnly && !string.IsNullOrWhiteSpace(text))
            {
                var ft = Text(text, tile ? Math.Clamp(cfg.FontSize * .65, 14, 28) : cfg.FontSize, brush, cfg.FontFamily, true);
                var ty = !tile && _image != null && cfg.ContentMode == WatermarkContentMode.TextAndImage ? cfg.ImageHeightPt / 2 + 24 : 0;
                dc.DrawText(ft, new Point(-ft.Width / 2, ty - ft.Height / 2));
            }
            dc.Pop(); dc.Pop();
        }
        if (cfg.PrimaryPlacement == WatermarkPlacement.TiledGrid)
        {
            var sx = Math.Max(90, cfg.TileStepX); var sy = Math.Max(70, cfg.TileStepY);
            for (double y = 40; y < h - 60; y += sy)
                for (double x = 40; x < w - 40; x += sx) Stamp(x + ((int)(y / sy) % 2 == 0 ? 0 : sx / 2), y, true);
        }
        else Stamp(w / 2, cfg.PrimaryPlacement switch { WatermarkPlacement.TopCenter => h * .2, WatermarkPlacement.BottomCenter => h * .78, _ => h / 2 }, false);
        var qrTop = h - Math.Max(28.35, cfg.BottomMarginPt) - 45;
        if (cfg.ShowQrCode)
        {
            if (_qr == null)
            {
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(StampCodes.EncryptPayload(raw), QRCodeGenerator.ECCLevel.Q);
                using var qr = new PngByteQRCode(data);
                _qr = DecodeImage(qr.GetGraphic(4));
            }
            dc.DrawImage(_qr, new Rect(36, qrTop, 45, 45));
        }
        if (cfg.ShowBottomAuditFooter)
            DrawText(dc, $"ID: {code} | User: {meta.UserName} | PC: {meta.ComputerName} | {time:yyyy-MM-dd HH:mm:ss} | Page 1/1", cfg.ShowQrCode ? 89 : 36, qrTop + 16.5, 8, Brushes.Black, true);
        dc.Pop();
    }
}
