using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using SinUpdateHub.App.Services;
using SinUpdateHub.App.ViewModels;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.Views;

/// <summary>基于 WebView2 + 离线 Vditor IR 的 Typora 风格 Markdown 编辑器宿主控件。</summary>
public partial class MarkdownNoteEditor : UserControl
{
    private const string VirtualHostName = "sinnote.local";
    private const string EditorUrl = $"https://{VirtualHostName}/editor.html";

    private bool _isInitializing;
    private bool _isCoreReady;
    private bool _isPageReady;
    private bool _isFailed;

    private string? _pendingPageId;
    private string _pendingMarkdown = string.Empty;
    private string _pendingHeaderColor = NoteTable.DefaultHeaderBgColor;
    private string _pendingFirstColColor = NoteTable.DefaultFirstColumnBgColor;
    private string _pendingCellColor = NoteTable.DefaultCellBgColor;
    private string _pendingFontFamily = AppSettings.DefaultNoteFontFamily;
    private int _pendingZoomPercent = AppSettings.DefaultNoteZoomPercent;
    private bool _pendingForceReload;

    public MarkdownNoteEditor()
    {
        InitializeComponent();

        EditorWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 13, 17, 23);
        Loaded += (_, _) => TryTriggerInitialize();
        IsVisibleChanged += (_, _) => TryTriggerInitialize();
    }

    public event Action<string, string>? ContentChanged;
    public event Action<string>? ShortcutRequested;
    public event Action? EditorFocused;
    public event Action<string, string, string, string>? ImageSavedRequested;
    public event Action<string, string, string>? TableColorsChanged;
    public event Action<string>? FontFamilyChanged;
    public event Action<int>? ZoomChanged;

    private void TryTriggerInitialize()
    {
        if (IsVisible && !_isCoreReady && !_isInitializing && !_isFailed)
        {
            _ = InitializeEditorAsync();
        }
    }

    public void LoadPage(NotePageItemViewModel? page, bool force = false)
    {
        if (page is null)
        {
            _pendingPageId = null;
            _pendingMarkdown = string.Empty;
            _pendingHeaderColor = NoteTable.DefaultHeaderBgColor;
            _pendingFirstColColor = NoteTable.DefaultFirstColumnBgColor;
            _pendingCellColor = NoteTable.DefaultCellBgColor;
        }
        else
        {
            _pendingPageId = page.Id;
            _pendingMarkdown = page.Page.EnsureMarkdown();
            VditorAssetHost.SyncPageImagesToCache(page.Page);
            _pendingHeaderColor = page.TableHeaderBgColor;
            _pendingFirstColColor = page.TableFirstColumnBgColor;
            _pendingCellColor = page.TableCellBgColor;
        }

        _pendingForceReload = _pendingForceReload || force;

        if (_isPageReady)
        {
            _ = SendLoadPageToJsAsync();
        }
    }

    public void UpdateTableColors(NotePageItemViewModel page)
    {
        _pendingHeaderColor = page.TableHeaderBgColor;
        _pendingFirstColColor = page.TableFirstColumnBgColor;
        _pendingCellColor = page.TableCellBgColor;

        if (!_isPageReady)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            headerBg = _pendingHeaderColor,
            firstColBg = _pendingFirstColColor,
            cellBg = _pendingCellColor,
        });
        _ = ExecuteScriptSafeAsync($"window.sinNoteApi && window.sinNoteApi.setTableColors({json});");
    }

    public void UpdateTypography(string fontFamily, int zoomPercent)
    {
        _pendingFontFamily = string.IsNullOrWhiteSpace(fontFamily) ? AppSettings.DefaultNoteFontFamily : fontFamily.Trim();
        _pendingZoomPercent = Math.Clamp(zoomPercent, 50, 200);

        if (!_isPageReady)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            fontFamily = _pendingFontFamily,
            zoomPercent = _pendingZoomPercent,
        });
        _ = ExecuteScriptSafeAsync($"window.sinNoteApi && window.sinNoteApi.setTypography({json});");
    }

    public void InsertMarkdown(string markdownSnippet)
    {
        if (string.IsNullOrEmpty(markdownSnippet) || !_isPageReady)
        {
            return;
        }

        var json = JsonSerializer.Serialize(markdownSnippet);
        _ = ExecuteScriptSafeAsync($"window.sinNoteApi && window.sinNoteApi.insertMarkdown({json});");
    }

    public void HighlightSearch(string query, int occurrenceIndex)
    {
        if (!_isPageReady)
        {
            return;
        }

        var queryJson = JsonSerializer.Serialize(query ?? string.Empty);
        _ = ExecuteScriptSafeAsync($"window.sinNoteApi && window.sinNoteApi.highlightSearch({queryJson}, {occurrenceIndex});");
    }

    public void ClearSearchHighlight()
    {
        if (!_isPageReady)
        {
            return;
        }

        _ = ExecuteScriptSafeAsync("window.sinNoteApi && window.sinNoteApi.clearSearchHighlight();");
    }

    public void FlushPendingEdits()
    {
        if (!_isPageReady)
        {
            return;
        }

        _ = ExecuteScriptSafeAsync("window.sinNoteApi && window.sinNoteApi.flush();");
    }

    public void FocusEditor()
    {
        if (!_isPageReady)
        {
            return;
        }

        EditorWebView.Focus();
        _ = ExecuteScriptSafeAsync("window.sinNoteApi && window.sinNoteApi.focusEditor();");
    }

    private async Task InitializeEditorAsync()
    {
        if (_isInitializing || _isCoreReady)
        {
            return;
        }

        _isInitializing = true;
        try
        {
            var assetRoot = await Task.Run(VditorAssetHost.EnsureAssetsExtracted);
            Directory.CreateDirectory(VditorAssetHost.WebView2UserDataDir);
            Directory.CreateDirectory(VditorAssetHost.NoteImagesDir);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: VditorAssetHost.WebView2UserDataDir);

            await EditorWebView.EnsureCoreWebView2Async(env);

            var core = EditorWebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;

            core.SetVirtualHostNameToFolderMapping(
                VirtualHostName,
                assetRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            core.SetVirtualHostNameToFolderMapping(
                VditorAssetHost.NoteImagesHostName,
                VditorAssetHost.NoteImagesDir,
                CoreWebView2HostResourceAccessKind.Allow);

            core.NavigationStarting += Core_NavigationStarting;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.WebMessageReceived += Core_WebMessageReceived;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.ProcessFailed += Core_ProcessFailed;

            _isCoreReady = true;
            core.Navigate(EditorUrl);
        }
        catch (Exception ex)
        {
            _isFailed = true;
            AppLog.Write("初始化 SinNote WebView2 编辑器失败", ex);
            FallbackTitleText.Text = "无法加载 Markdown 编辑器（需要 Microsoft Edge WebView2 Runtime）";
            FallbackDetailText.Text = ex.Message;
            FallbackOverlay.Visibility = Visibility.Visible;
            EditorWebView.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Host, VirtualHostName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, VditorAssetHost.NoteImagesHostName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        e.Cancel = true;
        OpenExternalUrl(e.Uri);
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternalUrl(e.Uri);
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            FallbackTitleText.Text = "Markdown 编辑器页面加载失败";
            FallbackDetailText.Text = $"错误代码：{e.WebErrorStatus}";
            FallbackOverlay.Visibility = Visibility.Visible;
        }
    }

    private void Core_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        AppLog.Write($"SinNote WebView2 进程异常（{e.ProcessFailedKind}），正在自动重载编辑器");
        _isPageReady = false;
        _pendingForceReload = true;
        try
        {
            EditorWebView.CoreWebView2?.Navigate(EditorUrl);
        }
        catch (Exception ex)
        {
            AppLog.Write("重载 SinNote WebView2 失败", ex);
        }
    }

    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp))
            {
                return;
            }

            var type = typeProp.GetString();
            switch (type)
            {
                case "ready":
                    _isPageReady = true;
                    FallbackOverlay.Visibility = Visibility.Collapsed;
                    EditorWebView.Visibility = Visibility.Visible;
                    _ = SendSystemFontsToJsAsync();
                    _ = SendLoadPageToJsAsync();
                    break;

                case "contentChanged":
                {
                    var pageId = root.TryGetProperty("pageId", out var p) ? p.GetString() : null;
                    var markdown = root.TryGetProperty("markdown", out var m) ? m.GetString() : null;
                    if (!string.IsNullOrEmpty(pageId) && markdown is not null)
                    {
                        ContentChanged?.Invoke(pageId, markdown);
                    }

                    break;
                }

                case "shortcut":
                {
                    var key = root.TryGetProperty("key", out var k) ? k.GetString() : null;
                    if (!string.IsNullOrEmpty(key))
                    {
                        ShortcutRequested?.Invoke(key);
                    }

                    break;
                }

                case "editorFocused":
                    EditorFocused?.Invoke();
                    break;

                case "saveImage":
                {
                    var pageId = root.TryGetProperty("pageId", out var p) ? p.GetString() : _pendingPageId;
                    var imageId = root.TryGetProperty("imageId", out var id) ? id.GetString() : null;
                    var mimeType = root.TryGetProperty("mimeType", out var mt) ? mt.GetString() : "image/png";
                    var base64 = root.TryGetProperty("base64", out var b64) ? b64.GetString() : null;
                    var alt = root.TryGetProperty("alt", out var a) ? a.GetString() : "image";

                    if (!string.IsNullOrWhiteSpace(base64))
                    {
                        var safeKey = VditorAssetHost.SanitizeImageKey(imageId) ?? $"img_{Guid.NewGuid():N}.png";
                        VditorAssetHost.EnsureImageCached(safeKey, base64);
                        if (!string.IsNullOrWhiteSpace(pageId))
                        {
                            ImageSavedRequested?.Invoke(pageId, safeKey, mimeType ?? "image/png", base64);
                        }

                        var altJson = JsonSerializer.Serialize(string.IsNullOrWhiteSpace(alt) ? "image" : alt);
                        var urlJson = JsonSerializer.Serialize($"https://{VditorAssetHost.NoteImagesHostName}/{safeKey}");
                        _ = ExecuteScriptSafeAsync($"window.sinNoteApi && window.sinNoteApi.insertImageUrl({altJson}, {urlJson});");
                    }

                    break;
                }

                case "tableColorsChanged":
                {
                    var headerBg = root.TryGetProperty("headerBg", out var h) ? h.GetString() : null;
                    var firstColBg = root.TryGetProperty("firstColBg", out var fc) ? fc.GetString() : null;
                    var cellBg = root.TryGetProperty("cellBg", out var c) ? c.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(headerBg))
                    {
                        _pendingHeaderColor = headerBg;
                    }

                    if (!string.IsNullOrWhiteSpace(firstColBg))
                    {
                        _pendingFirstColColor = firstColBg;
                    }

                    if (!string.IsNullOrWhiteSpace(cellBg))
                    {
                        _pendingCellColor = cellBg;
                    }

                    TableColorsChanged?.Invoke(
                        _pendingHeaderColor,
                        _pendingFirstColColor,
                        _pendingCellColor);
                    break;
                }

                case "fontFamilyChanged":
                {
                    var fontFamily = root.TryGetProperty("fontFamily", out var ff) ? ff.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(fontFamily))
                    {
                        _pendingFontFamily = fontFamily.Trim();
                        FontFamilyChanged?.Invoke(_pendingFontFamily);
                    }

                    break;
                }

                case "openLink":
                {
                    var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        OpenExternalUrl(url);
                    }

                    break;
                }

                case "zoomChanged":
                {
                    if (root.TryGetProperty("zoom", out var z) && z.TryGetInt32(out var zoomPercent))
                    {
                        _pendingZoomPercent = Math.Clamp(zoomPercent, 50, 200);
                        ZoomChanged?.Invoke(_pendingZoomPercent);
                    }

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("处理 Markdown 编辑器消息失败", ex);
        }
    }

    private async Task SendSystemFontsToJsAsync()
    {
        if (!_isPageReady)
        {
            return;
        }

        try
        {
            var fonts = await Task.Run(CollectSystemFonts);
            var json = JsonSerializer.Serialize(fonts);
            await ExecuteScriptSafeAsync($"window.sinNoteApi && window.sinNoteApi.setSystemFonts({json});");
        }
        catch (Exception ex)
        {
            AppLog.Write("加载系统字体列表失败", ex);
        }
    }

    private static List<object> CollectSystemFonts()
    {
        var zhCn = XmlLanguage.GetLanguage("zh-cn");
        var zhHans = XmlLanguage.GetLanguage("zh-hans");
        var enUs = XmlLanguage.GetLanguage("en-us");

        var excludedPrefixes = new[]
        {
            "Segoe MDL2",
            "Segoe Fluent",
            "HoloLens MDL2",
            "Webdings",
            "Wingdings",
            "Symbol",
            "Marlett",
            "Bookshelf Symbol",
            "MT Extra",
            "MS Outlook",
            "MS Reference Specialty",
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<(string DisplayName, string CssValue, bool IsChinese)>();

        foreach (var ff in Fonts.SystemFontFamilies)
        {
            var enName = ff.FamilyNames.TryGetValue(enUs, out var en)
                ? en
                : (ff.Source ?? ff.FamilyNames.Values.FirstOrDefault() ?? string.Empty);
            enName = enName.Trim();
            if (string.IsNullOrWhiteSpace(enName) || enName.StartsWith('@'))
            {
                continue;
            }

            if (excludedPrefixes.Any(p => enName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var zhName = ff.FamilyNames.TryGetValue(zhCn, out var z1)
                ? z1.Trim()
                : (ff.FamilyNames.TryGetValue(zhHans, out var z2) ? z2.Trim() : string.Empty);

            var displayName = !string.IsNullOrWhiteSpace(zhName) ? zhName : enName;
            if (!seen.Add(displayName))
            {
                continue;
            }

            var hasCjk = displayName.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);
            var cssParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(zhName))
            {
                cssParts.Add($"'{zhName.Replace("\"", string.Empty).Replace("'", string.Empty)}'");
            }

            if (!string.Equals(enName, zhName, StringComparison.OrdinalIgnoreCase))
            {
                cssParts.Add($"'{enName.Replace("\"", string.Empty).Replace("'", string.Empty)}'");
            }

            cssParts.Add("'Microsoft YaHei UI'");
            cssParts.Add("sans-serif");
            var cssValue = string.Join(", ", cssParts);
            items.Add((displayName, cssValue, hasCjk));
        }

        // 中文常用字体排前面，其余字体按字母排序
        return items
            .OrderByDescending(i => i.IsChinese)
            .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => (object)new { name = i.DisplayName, css = i.CssValue })
            .ToList();
    }

    private async Task SendLoadPageToJsAsync()
    {
        if (!_isPageReady)
        {
            return;
        }

        var payload = new
        {
            pageId = _pendingPageId,
            markdown = _pendingMarkdown,
            force = _pendingForceReload,
            colors = new
            {
                headerBg = _pendingHeaderColor,
                firstColBg = _pendingFirstColColor,
                cellBg = _pendingCellColor,
            },
            typography = new
            {
                fontFamily = _pendingFontFamily,
                zoomPercent = _pendingZoomPercent,
            },
        };
        _pendingForceReload = false;

        var json = JsonSerializer.Serialize(payload);
        await ExecuteScriptSafeAsync($"window.sinNoteApi && window.sinNoteApi.loadPage({json});");
    }

    private async Task ExecuteScriptSafeAsync(string script)
    {
        if (!_isCoreReady || EditorWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await EditorWebView.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            AppLog.Write("执行 Markdown 编辑器脚本失败", ex);
        }
    }

    private static void OpenExternalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write("打开外部链接失败", ex);
        }
    }
}
