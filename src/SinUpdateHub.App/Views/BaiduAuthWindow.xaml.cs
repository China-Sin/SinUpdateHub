using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.Views;

/// <summary>内置 WebView2 的百度网盘 OAuth2 授权登录窗口，捕获授权码 code 后自动关闭。</summary>
public partial class BaiduAuthWindow : Window
{
    private readonly string _authorizeUrl;
    private bool _completed;

    public BaiduAuthWindow(string appKey)
    {
        InitializeComponent();
        _authorizeUrl = BaiduPanService.BuildAuthorizeUrl(appKey);
        Loaded += BaiduAuthWindow_Loaded;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Cancel_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
    }

    public string? AuthCode { get; private set; }

    private async void BaiduAuthWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SinUpdateHub",
                "webview2-baidu");
            Directory.CreateDirectory(userDataDir);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await AuthWebView.EnsureCoreWebView2Async(env);

            AuthWebView.NavigationStarting += (_, args) => TryCaptureCode(args.Uri);
            AuthWebView.SourceChanged += (_, _) => TryCaptureCode(AuthWebView.Source?.AbsoluteUri);
            AuthWebView.NavigationCompleted += async (_, _) =>
            {
                TryCaptureCode(AuthWebView.Source?.AbsoluteUri);
                if (!_completed && AuthWebView.Source is { } uri
                    && uri.Host.Equals("openapi.baidu.com", StringComparison.OrdinalIgnoreCase))
                {
                    await TryCaptureCodeFromPageAsync();
                }
            };

            AuthWebView.Source = new Uri(_authorizeUrl);
        }
        catch (Exception ex)
        {
            AppLog.Write("初始化百度授权 WebView2 失败", ex);
            StatusHintText.Text = "WebView2 加载失败：" + ex.Message;
        }
    }

    private void TryCaptureCode(string? rawUrl)
    {
        if (_completed || string.IsNullOrWhiteSpace(rawUrl))
        {
            return;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        var code = BaiduPanService.TryExtractAuthCode(uri);
        if (!string.IsNullOrWhiteSpace(code))
        {
            CompleteWithCode(code);
        }
    }

    private async Task TryCaptureCodeFromPageAsync()
    {
        try
        {
            // 百度 oob 授权页会在页面上渲染一个装有 32 位十六进制授权码的 input 框（旁边是「复制授权码」按钮）
            var script = @"(function(){
                var inputs = document.querySelectorAll('input');
                for (var i = 0; i < inputs.length; i++) {
                    var v = (inputs[i].value || '').trim();
                    if (/^[a-f0-9]{24,64}$/i.test(v)) return v;
                }
                var bodyText = document.body ? (document.body.innerText || '') : '';
                if (bodyText.indexOf('授权码') >= 0) {
                    var m = bodyText.match(/\b[a-f0-9]{24,64}\b/i);
                    if (m) return m[0];
                }
                return '';
            })()";
            var json = await AuthWebView.CoreWebView2.ExecuteScriptAsync(script);
            var extracted = System.Text.Json.JsonSerializer.Deserialize<string>(json)?.Trim();
            if (!string.IsNullOrWhiteSpace(extracted) && extracted.Length >= 24)
            {
                CompleteWithCode(extracted);
            }
        }
        catch
        {
            // ignore script errors
        }
    }

    private void CompleteWithCode(string code)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        AuthCode = code.Trim();
        DialogResult = true;
        Close();
    }

    private void ConfirmManualCode_Click(object sender, RoutedEventArgs e)
    {
        var code = ManualCodeBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            StatusHintText.Text = "请先输入或粘贴授权码";
            return;
        }

        CompleteWithCode(code);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_completed)
        {
            DialogResult = false;
        }

        Close();
    }
}
