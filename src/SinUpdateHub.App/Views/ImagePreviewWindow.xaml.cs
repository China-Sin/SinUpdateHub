using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SinUpdateHub.App.Services;

namespace SinUpdateHub.App.Views;

/// <summary>高清查看大图弹窗（支持适应窗口 / 100% 原图切换与复制）。</summary>
public partial class ImagePreviewWindow : Window
{
    private readonly string? _base64;
    private bool _actualSize;

    public ImagePreviewWindow(BitmapSource image, string? caption = null, string? base64 = null)
    {
        InitializeComponent();

        _base64 = base64;
        PreviewImageControl.Source = image;
        var info = $"{image.PixelWidth}×{image.PixelHeight}";
        CaptionTextBlock.Text = string.IsNullOrWhiteSpace(caption)
            ? $"图片预览（{info}）"
            : $"{caption}（{info}）";
    }

    private void ToggleZoomMode_Click(object sender, RoutedEventArgs e)
    {
        _actualSize = !_actualSize;
        if (_actualSize)
        {
            PreviewScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            PreviewScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            PreviewImageControl.Stretch = Stretch.None;
            ZoomModeButton.Content = "适应窗口";
        }
        else
        {
            PreviewScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            PreviewScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            PreviewImageControl.Stretch = Stretch.Uniform;
            PreviewImageControl.StretchDirection = StretchDirection.DownOnly;
            ZoomModeButton.Content = "查看 100% 原图";
        }
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_base64) && NoteImageHelper.TryCopyBase64ToClipboard(_base64))
        {
            ZoomModeButton.Focus();
            return;
        }

        if (PreviewImageControl.Source is BitmapSource src)
        {
            try
            {
                Clipboard.SetImage(src);
            }
            catch
            {
                // ignore
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // ignore
            }
        }
    }
}
