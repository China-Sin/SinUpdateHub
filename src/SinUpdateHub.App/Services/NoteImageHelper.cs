using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.Services;

/// <summary>笔记图片编解码、剪贴板读写及表格自适应文字色辅助类。</summary>
public static class NoteImageHelper
{
    private const int CompressThresholdBytes = 450 * 1024;
    private static readonly SolidColorBrush DarkTextBrush = FreezeBrush(Color.FromRgb(0x11, 0x18, 0x27));
    private static readonly SolidColorBrush LightTextBrush = FreezeBrush(Color.FromRgb(0xF0, 0xF6, 0xFC));

    /// <summary>把剪贴板或内存位图编码为 Base64（优先 PNG，超过 450 KB 时尝试 JPEG 取更小者）。</summary>
    public static (string Base64, string MimeType) EncodeBitmapSource(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var pngBytes = EncodeBytes(source, new PngBitmapEncoder());
        var bestBytes = pngBytes;
        var mime = "image/png";

        if (pngBytes.Length > CompressThresholdBytes)
        {
            var jpegBytes = EncodeBytes(source, new JpegBitmapEncoder { QualityLevel = 85 });
            if (jpegBytes.Length > 0 && jpegBytes.Length < pngBytes.Length)
            {
                bestBytes = jpegBytes;
                mime = "image/jpeg";
            }
        }

        return (Convert.ToBase64String(bestBytes), mime);
    }

    /// <summary>从本地图片文件读取或转换编码为 Base64 与 MIME 类型。</summary>
    public static (string Base64, string MimeType) EncodeImageFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到指定的图片文件", filePath);
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        string? directMime = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null,
        };

        if (directMime is not null)
        {
            var raw = File.ReadAllBytes(filePath);
            return (Convert.ToBase64String(raw), directMime);
        }

        var decoder = BitmapDecoder.Create(
            new Uri(filePath, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault()
                    ?? throw new InvalidOperationException("无法读取图片帧内容");
        return EncodeBitmapSource(frame);
    }

    /// <summary>将 Base64 字符串解码为可安全跨线程绑定显示的冻结 BitmapImage。</summary>
    public static BitmapImage? DecodeBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            AppLog.Write("解码笔记图片失败", ex);
            return null;
        }
    }

    private static readonly string[] SupportedImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp",
    };

    /// <summary>尝试判断剪贴板是否包含非空纯文本（毫秒级非阻塞，用于区分粘贴文本与粘贴图片）。</summary>
    public static bool TryClipboardHasNonEmptyText()
        => Win32Clipboard.TryGetText(out var text) && !string.IsNullOrWhiteSpace(text);

    /// <summary>尝试从系统剪贴板读取图片（先瞬时探测图片格式再单次读取，避免无图或占用时卡顿）。</summary>
    public static BitmapSource? TryGetClipboardImage()
    {
        if (!Win32Clipboard.HasAnyImageFormat())
        {
            return null;
        }

        try
        {
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is not null)
                {
                    if (image.CanFreeze && !image.IsFrozen)
                    {
                        image.Freeze();
                    }

                    return image;
                }
            }

            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is Stream pngStream)
            {
                using (pngStream)
                {
                    var decoder = BitmapDecoder.Create(
                        pngStream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames.FirstOrDefault();
                    if (frame is not null)
                    {
                        if (frame.CanFreeze && !frame.IsFrozen)
                        {
                            frame.Freeze();
                        }

                        return frame;
                    }
                }
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files is { Count: 1 })
                {
                    var filePath = files[0];
                    if (!string.IsNullOrWhiteSpace(filePath)
                        && File.Exists(filePath)
                        && SupportedImageExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase))
                    {
                        var decoder = BitmapDecoder.Create(
                            new Uri(filePath, UriKind.Absolute),
                            BitmapCreateOptions.PreservePixelFormat,
                            BitmapCacheOption.OnLoad);
                        var frame = decoder.Frames.FirstOrDefault();
                        if (frame is not null)
                        {
                            if (frame.CanFreeze && !frame.IsFrozen)
                            {
                                frame.Freeze();
                            }

                            return frame;
                        }
                    }
                }
            }

            return null;
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Write("读取剪贴板图片失败", ex);
            return null;
        }
    }

    /// <summary>将 Base64 图片写回系统剪贴板。</summary>
    public static bool TryCopyBase64ToClipboard(string? base64)
    {
        var image = DecodeBase64(base64);
        if (image is null)
        {
            return false;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                return true;
            }
            catch (ExternalException)
            {
                if (attempt < 4)
                {
                    Thread.Sleep(15);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("复制笔记图片到剪贴板失败", ex);
                return false;
            }
        }

        return false;
    }

    /// <summary>规范化十六进制颜色串（支持 #RRGGBB 或 #AARRGGBB，可省略前导 #）；无效返回 null。</summary>
    public static string? NormalizeHexOrNull(string? hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor))
        {
            return null;
        }

        var trimmed = hexColor.Trim();
        if (!trimmed.StartsWith('#'))
        {
            trimmed = "#" + trimmed;
        }

        var hex = trimmed[1..];
        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return "#" + hex.ToUpperInvariant();
        }

        if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return "#" + hex.ToUpperInvariant();
        }

        if (hex.Length == 3 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            var expanded = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            return "#" + expanded.ToUpperInvariant();
        }

        return null;
    }

    /// <summary>解析十六进制颜色并返回冻结的 SolidColorBrush；解析失败时回退到 fallbackHex。</summary>
    public static SolidColorBrush ParseBrushOrFallback(string? hexColor, string fallbackHex)
    {
        if (TryParseColor(hexColor, out var color))
        {
            return FreezeBrush(color);
        }

        if (TryParseColor(fallbackHex, out var fallbackColor))
        {
            return FreezeBrush(fallbackColor);
        }

        return FreezeBrush(Color.FromRgb(0x2F, 0x75, 0xB5));
    }

    /// <summary>根据单元格背景亮度自动返回高对比度深/浅前景色画刷。</summary>
    public static SolidColorBrush GetContrastingTextBrush(string? hexColor)
    {
        if (!TryParseColor(hexColor, out var c))
        {
            return LightTextBrush;
        }

        var luminance = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return luminance > 155 ? DarkTextBrush : LightTextBrush;
    }

    private static bool TryParseColor(string? hexColor, out Color color)
    {
        color = default;
        var normalized = NormalizeHexOrNull(hexColor);
        if (normalized is null)
        {
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(normalized) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static byte[] EncodeBytes(BitmapSource source, BitmapEncoder encoder)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static SolidColorBrush FreezeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
