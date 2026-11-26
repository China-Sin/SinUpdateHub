using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// HttpClient 工厂：处理代理（跟随系统 / 手动 / 不使用）、User-Agent、自动解压、GitHub Token。
/// 按超时时长分别缓存客户端，设置变化时重新创建客户端；GitHub Token 仅在请求 api.github.com 时附加。
/// </summary>
public sealed class NetworkClientFactory : IDisposable
{
    public const string UserAgent = "SinUpdateHub/1.0 (Windows; +https://github.com/SinUpdateHub)";

    private readonly Func<AppSettings> _settingsAccessor;
    private readonly object _gate = new();
    private readonly Dictionary<TimeSpan, HttpClient> _clients = new();
    private string _settingsSignature = string.Empty;

    public NetworkClientFactory(Func<AppSettings> settingsAccessor)
    {
        _settingsAccessor = settingsAccessor;
    }

    /// <summary>常规 API 请求客户端（超时 30 秒）。</summary>
    public HttpClient Client => GetClient(TimeSpan.FromSeconds(30));

    /// <summary>大文件下载客户端（超时 30 分钟）。</summary>
    public HttpClient DownloadClient => GetClient(TimeSpan.FromMinutes(30));

    public HttpClient GetClient(TimeSpan timeout)
    {
        var settings = _settingsAccessor();
        var settingsSignature = string.Join('|',
            settings.ProxyMode,
            settings.ProxyUrl,
            settings.GitHubToken);

        lock (_gate)
        {
            if (_settingsSignature != settingsSignature)
            {
                // 设置变更时只清空缓存引用而不强制 Dispose 旧实例，避免掐断变更前已发起的在途请求
                _clients.Clear();
                _settingsSignature = settingsSignature;
            }

            if (_clients.TryGetValue(timeout, out var existing))
            {
                return existing;
            }

            var created = CreateClient(settings, timeout);
            _clients[timeout] = created;
            return created;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }

    /// <summary>按设置创建一个新的 HttpClient（不复用，供一次性场景）。</summary>
    public static HttpClient CreateClient(AppSettings settings, TimeSpan timeout)
    {
        var (handler, proxyDescription) = CreateHandler(settings);
        handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli;
        handler.AllowAutoRedirect = true;
        handler.MaxAutomaticRedirections = 10;

        var authHandler = new GitHubAuthHandler(handler, settings.GitHubToken);
        var client = new HttpClient(authHandler, disposeHandler: true)
        {
            Timeout = timeout,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        AppLog.Write($"HTTP 客户端就绪（代理：{proxyDescription}，超时：{timeout.TotalSeconds:0}s）");
        return client;
    }

    /// <summary>仅在请求 https://api.github.com 时附加 GitHub API 请求头与 Token，避免泄漏给第三方服务器。</summary>
    internal static void ApplyGitHubHeaders(HttpRequestMessage request, string? token)
    {
        if (request.RequestUri is not { Scheme: "https" } uri
            || !uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 若调用方已明确指定 Accept（例如下载私密仓库 Release 附件时的 application/octet-stream），保留调用方设定
        if (request.Headers.Accept.Count == 0)
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
        }

        if (!request.Headers.Contains("X-GitHub-Api-Version"))
        {
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        }

        if (!string.IsNullOrWhiteSpace(token) && request.Headers.Authorization is null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }
    }

    private sealed class GitHubAuthHandler : DelegatingHandler
    {
        private readonly string? _token;

        public GitHubAuthHandler(HttpMessageHandler innerHandler, string? token)
            : base(innerHandler)
        {
            _token = token;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApplyGitHubHeaders(request, _token);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static (HttpClientHandler Handler, string Description) CreateHandler(AppSettings settings)
    {
        var handler = new HttpClientHandler { UseCookies = false };

        switch (settings.ProxyMode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                return (handler, "不使用");

            case ProxyMode.Custom:
            {
                var url = (settings.ProxyUrl ?? string.Empty).Trim();
                if (url.Length == 0)
                {
                    handler.UseProxy = false;
                    return (handler, "手动模式但未填地址 → 直连");
                }

                if (!url.Contains("://", StringComparison.Ordinal))
                {
                    url = "http://" + url;
                }

                if (Uri.TryCreate(url, UriKind.Absolute, out var proxyUri))
                {
                    handler.UseProxy = true;
                    handler.Proxy = new WebProxy(proxyUri);
                    return (handler, url);
                }

                handler.UseProxy = false;
                return (handler, $"代理地址无法解析（{url}）→ 直连");
            }

            default:
            {
                var system = ReadSystemProxy();
                if (system is not null)
                {
                    handler.UseProxy = true;
                    handler.Proxy = system;
                    return (handler, "系统代理 " + system.Address);
                }

                handler.UseProxy = true;
                handler.Proxy = HttpClient.DefaultProxy;
                return (handler, "系统默认");
            }
        }
    }

    /// <summary>从注册表读系统代理（IE 设置）。没开代理返回 null。</summary>
    public static WebProxy? ReadSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null)
            {
                return null;
            }

            var enabled = key.GetValue("ProxyEnable") is int i && i != 0;
            var server = key.GetValue("ProxyServer") as string;
            if (!enabled || string.IsNullOrWhiteSpace(server))
            {
                return null;
            }

            // 可能是 "127.0.0.1:7897" 或 "http=127.0.0.1:7897;https=127.0.0.1:7897"
            var address = server.Trim();
            if (address.Contains('='))
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in address.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length == 2)
                    {
                        dict[kv[0].Trim()] = kv[1].Trim();
                    }
                }

                if (dict.TryGetValue("https", out var https))
                {
                    address = https;
                }
                else if (dict.TryGetValue("http", out var http))
                {
                    address = http;
                }
                else
                {
                    address = dict.Values.FirstOrDefault() ?? address;
                }
            }

            if (!address.Contains("://", StringComparison.Ordinal))
            {
                address = "http://" + address;
            }

            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var proxy = new WebProxy(uri);

            var bypass = key.GetValue("ProxyOverride") as string;
            ApplyBypassList(proxy, bypass);

            return proxy;
        }
        catch (Exception ex)
        {
            AppLog.Write("读取系统代理失败", ex);
            return null;
        }
    }

    /// <summary>把 Windows 系统代理的 ProxyOverride 通配符规则转成正则并写入 WebProxy.BypassList。</summary>
    internal static void ApplyBypassList(WebProxy proxy, string? bypass)
    {
        if (string.IsNullOrWhiteSpace(bypass))
        {
            return;
        }

        var rawEntries = bypass
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);

        var validEntries = new List<string>();
        var skipped = new List<string>();

        foreach (var rawEntry in rawEntries)
        {
            if (rawEntry.Equals("<local>", StringComparison.OrdinalIgnoreCase))
            {
                proxy.BypassProxyOnLocal = true;
                continue;
            }

            var entry = rawEntry.TrimEnd('/');
            if (entry.Length == 0)
            {
                continue;
            }

            var escaped = Regex.Escape(entry).Replace(@"\*", ".*").Replace(@"\?", ".");
            var pattern = entry.Contains("://", StringComparison.Ordinal)
                ? "^" + escaped + @"(?::\d+)?$"
                : @"^[a-zA-Z][a-zA-Z0-9+\-.]*://" + escaped + @"(?::\d+)?$";

            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase);
                validEntries.Add(pattern);
            }
            catch
            {
                skipped.Add(rawEntry);
            }
        }

        if (validEntries.Count > 0)
        {
            try
            {
                proxy.BypassList = validEntries.ToArray();
            }
            catch (Exception ex)
            {
                AppLog.Write($"应用系统代理 BypassList 失败，已忽略：{bypass}", ex);
            }
        }

        if (skipped.Count > 0)
        {
            AppLog.Write($"系统代理 BypassList 含不合法通配符，已忽略部分条目：{string.Join(';', skipped)}");
        }
    }
}
