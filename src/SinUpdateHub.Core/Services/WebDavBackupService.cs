using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// WebDAV 备份：连接测试、上传（每次一条时间戳快照，按 KeepBackups 自动清理旧快照）、
/// 列出 / 下载 / 删除远端备份。使用 Basic 认证，请求走 <see cref="NetworkClientFactory.Client"/>（自动跟随代理）。
/// 所有可预期的网络/权限错误都转换成中文可操作提示。
/// </summary>
public sealed class WebDavBackupService : IWebDavBackupService
{
    private const string PropFindBody =
        "<?xml version=\"1.0\"?><D:propfind xmlns:D=\"DAV:\"><D:prop><D:resourcetype/><D:getcontentlength/><D:getlastmodified/></D:prop></D:propfind>";

    private static readonly XNamespace Dav = "DAV:";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private const string LatestFileName = "data.json";

    private readonly NetworkClientFactory _factory;

    /// <param name="factory">HTTP 客户端工厂（负责代理 / User-Agent）。</param>
    /// <param name="settingsAccessor">
    /// 仅为兼容 WPF 侧"所有服务统一注入设置"的构造方式而保留：接口里每个方法都显式传入 settings，
    /// 这里不读取该访问器（代理与 Token 由工厂内部读取设置）。
    /// </param>
    public WebDavBackupService(NetworkClientFactory factory, Func<AppSettings>? settingsAccessor = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _ = settingsAccessor;
    }

    /// <summary>
    /// 测试连接：先 MKCOL 远程目录（已存在时服务器返回 405，也视为成功），
    /// 再 PROPFIND Depth 0 确认目录可读。失败信息覆盖 401/403、404、超时、代理等常见原因。
    /// </summary>
    public async Task<WebDavCheckResult> CheckAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string folderUrl;
        try
        {
            folderUrl = BuildFolderUrl(settings);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }

        try
        {
            var client = _factory.Client;

            using (var mkcol = await SendAsync(client, new HttpMethod("MKCOL"), folderUrl, settings, null, ct)
                       .ConfigureAwait(false))
            {
                if (!mkcol.IsSuccessStatusCode && mkcol.StatusCode != HttpStatusCode.MethodNotAllowed)
                {
                    return Fail(MapStatus(mkcol.StatusCode, "创建远程目录"));
                }
            }

            using var propfind = await PropFindAsync(client, folderUrl, settings, 0, ct).ConfigureAwait(false);
            if (!propfind.IsSuccessStatusCode)
            {
                return Fail(MapStatus(propfind.StatusCode, "读取远程目录"));
            }

            return new WebDavCheckResult
            {
                Ok = true,
                Message = $"连接成功（账号：{settings.WebDavUser}），远程目录：{folderUrl}",
                RemoteFolder = folderUrl,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail("连接超时（30 秒无响应），请检查地址与代理设置");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Write("WebDAV 连接失败", ex);
            return Fail($"无法连接 WebDAV，请检查地址与代理设置（{ex.Message}）");
        }
        catch (Exception ex)
        {
            AppLog.Write("WebDAV 连接测试异常", ex);
            return Fail($"WebDAV 检查失败：{ex.Message}");
        }
    }

    /// <summary>列出远端目录下所有 .json 备份文件，按修改时间倒序（新的在前）。</summary>
    public Task<IReadOnlyList<BackupFileInfo>> ListAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return GuardAsync(() => ListCoreAsync(settings, ct), "列出备份", ct);
    }

    /// <summary>
    /// 上传清单：写一份 latest（data.json，覆盖），可选再写一份时间戳快照；
    /// 成功后按 <see cref="AppSettings.KeepBackups"/> 清理最旧的时间戳备份（0 = 不清理）。
    /// 失败时返回 Ok=false + 中文原因，不抛异常（便于界面直接展示）。
    /// </summary>
    public async Task<BackupUploadResult> UploadAsync(
        AppSettings settings,
        AppData data,
        bool makeTimestamped = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            var folderUrl = BuildFolderUrl(settings);
            var client = _factory.GetClient(TimeSpan.FromMinutes(2));
            var json = JsonSerializer.Serialize(BackupDocument.From(data), JsonConfig.Options);
            var size = Utf8NoBom.GetByteCount(json);

            // 一次备份只落一个文件：默认写时间戳快照（历史可回溯，按 KeepBackups 清理旧快照）；
            // 不再额外写 data.json 副本，避免「点一次备份出现两条记录」。
            var fileName = makeTimestamped
                ? $"data-{DateTime.Now:yyyyMMdd-HHmmss}.json"
                : LatestFileName;

            await PutAsync(client, folderUrl + Uri.EscapeDataString(fileName), settings, json, ct).ConfigureAwait(false);
            var uploaded = new BackupFileInfo
            {
                FileName = fileName,
                RemoteUrl = folderUrl + Uri.EscapeDataString(fileName),
                Size = size,
                LastModified = DateTimeOffset.Now,
            };

            // 清理旧版本遗留的 data.json 副本（忽略失败，不影响本次备份）
            if (makeTimestamped)
            {
                try
                {
                    await DeleteCoreAsync(settings, LatestFileName, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Write("清理旧的 data.json 副本失败（忽略）", ex);
                }
            }

            var pruned = await PruneAsync(settings, ct).ConfigureAwait(false);
            var message = $"备份成功：{uploaded.FileName}";

            return new BackupUploadResult
            {
                Ok = true,
                Message = message,
                File = uploaded,
                PrunedCount = pruned,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write("WebDAV 备份失败", ex);
            return new BackupUploadResult
            {
                Ok = false,
                Message = ex switch
                {
                    OperationCanceledException => "备份超时（30 秒无响应），请检查地址与代理设置",
                    HttpRequestException http => $"无法连接 WebDAV，请检查地址与代理设置（{http.Message}）",
                    InvalidOperationException invalid => invalid.Message,
                    _ => $"备份失败：{ex.Message}",
                },
            };
        }
    }

    /// <summary>
    /// 下载指定备份文件并还原成 <see cref="AppData"/>：
    /// 先按 <see cref="BackupDocument"/> 解析，其次按 <see cref="AppData"/> 解析；失败抛中文异常。
    /// </summary>
    public Task<AppData> DownloadAsync(AppSettings settings, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("备份文件名不能为空", nameof(fileName));
        }

        return GuardAsync(() => DownloadCoreAsync(settings, fileName.Trim(), ct), "下载备份", ct);
    }

    /// <summary>删除远端备份文件（404 视为已删除，按成功处理）。</summary>
    public Task DeleteAsync(AppSettings settings, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("备份文件名不能为空", nameof(fileName));
        }

        return GuardAsync(() => DeleteCoreAsync(settings, fileName.Trim(), ct), "删除备份", ct);
    }

    // ---------------- 内部实现 ----------------

    private async Task<IReadOnlyList<BackupFileInfo>> ListCoreAsync(AppSettings settings, CancellationToken ct)
    {
        var folderUrl = BuildFolderUrl(settings);
        var client = _factory.Client;
        using var response = await PropFindAsync(client, folderUrl, settings, 1, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(MapStatus(response.StatusCode, "列出备份"));
        }

        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var files = ParseListing(xml, new Uri(folderUrl));

        // 兼容旧版本留下的 data.json 副本：不在列表里展示，避免一次备份看起来像两条记录
        return files
            .Where(f => !string.Equals(f.FileName, LatestFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<AppData> DownloadCoreAsync(AppSettings settings, string fileName, CancellationToken ct)
    {
        var fileUrl = BuildFileUrl(settings, fileName);
        using var response = await SendAsync(_factory.GetClient(TimeSpan.FromMinutes(2)), HttpMethod.Get, fileUrl, settings, null, ct)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"远端没有找到备份文件「{fileName}」（HTTP 404），请刷新列表后重试");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(MapStatus(response.StatusCode, "下载备份"));
        }

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"备份文件格式不正确：{fileName} 根节点不是对象");
            }

            if (root.TryGetProperty("app", out _) || root.TryGetProperty("exportedAt", out _))
            {
                var backup = JsonSerializer.Deserialize<BackupDocument>(text, JsonConfig.Options)
                             ?? throw new InvalidOperationException($"备份文件内容为空：{fileName}");
                return backup.ToAppData();
            }

            var appData = JsonSerializer.Deserialize<AppData>(text, JsonConfig.Options)
                          ?? throw new InvalidOperationException($"备份文件内容为空：{fileName}");
            appData.Entries = (appData.Entries ?? new List<AppEntry>()).Where(e => e is not null).ToList();
            appData.NoteSections = NoteSection.NormalizeList(appData.NoteSections);

            return appData;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"备份文件格式不正确：{fileName} 不是 SinUpdateHub 的备份（JSON 解析失败）", ex);
        }
    }

    private async Task DeleteCoreAsync(AppSettings settings, string fileName, CancellationToken ct)
    {
        var fileUrl = BuildFileUrl(settings, fileName);
        using var response = await SendAsync(_factory.Client, HttpMethod.Delete, fileUrl, settings, null, ct)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(MapStatus(response.StatusCode, "删除备份"));
        }
    }

    /// <summary>按 KeepBackups 清理旧的时间戳备份（data.json 永远保留）。</summary>
    private async Task<int> PruneAsync(AppSettings settings, CancellationToken ct)
    {
        if (settings.KeepBackups <= 0)
        {
            return 0;
        }

        try
        {
            var files = await ListCoreAsync(settings, ct).ConfigureAwait(false);
            var snapshots = files
                .Where(f => !string.Equals(f.FileName, LatestFileName, StringComparison.OrdinalIgnoreCase)
                            && f.FileName.StartsWith("data-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastModified ?? DateTimeOffset.MinValue)
                .ThenByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var pruned = 0;
            for (var index = settings.KeepBackups; index < snapshots.Count; index++)
            {
                await DeleteCoreAsync(settings, snapshots[index].FileName, ct).ConfigureAwait(false);
                pruned++;
            }

            return pruned;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write("清理旧备份失败（不影响本次上传）", ex);
            return 0;
        }
    }

    /// <summary>PROPFIND 请求（Depth 由参数决定）。</summary>
    private async Task<HttpResponseMessage> PropFindAsync(
        HttpClient client,
        string url,
        AppSettings settings,
        int depth,
        CancellationToken ct)
    {
        using var content = new StringContent(PropFindBody, Utf8NoBom, "application/xml");
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url) { Content = content };
        request.Headers.TryAddWithoutValidation("Depth", depth.ToString(CultureInfo.InvariantCulture));
        AddAuthorization(request, settings);
        return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        AppSettings settings,
        HttpContent? content,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        AddAuthorization(request, settings);
        return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
    }

    private async Task PutAsync(
        HttpClient client,
        string url,
        AppSettings settings,
        string json,
        CancellationToken ct)
    {
        using var content = new StringContent(json, Utf8NoBom, "application/json");
        using var response = await SendAsync(client, HttpMethod.Put, url, settings, content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(MapStatus(response.StatusCode, "上传备份"));
        }
    }

    private static void AddAuthorization(HttpRequestMessage request, AppSettings settings)
    {
        var user = (settings.WebDavUser ?? string.Empty).Trim();
        var password = settings.WebDavPassword ?? string.Empty;
        if (user.Length == 0 && password.Length == 0)
        {
            return;
        }

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    /// <summary>基础地址规范化：去空白、缺 scheme 补 https://、末尾补 /。</summary>
    private static string NormalizeBaseUrl(string? url)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("WebDAV 地址未配置，请先在设置里填写（如 https://dav.example.com/dav/）");
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException($"WebDAV 地址格式不正确：{url}，示例：https://dav.example.com/dav/");
        }

        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    /// <summary>完整目录 URL = 基础地址 + 按段 URL 编码的子目录 + /。</summary>
    private static string BuildFolderUrl(AppSettings settings)
    {
        var baseUrl = NormalizeBaseUrl(settings.WebDavUrl);
        var folder = (settings.WebDavFolder ?? string.Empty).Trim().Trim('/');
        if (folder.Length == 0)
        {
            return baseUrl;
        }

        var encoded = string.Join(
            "/",
            folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return baseUrl + encoded + "/";
    }

    private static string BuildFileUrl(AppSettings settings, string fileName)
        => BuildFolderUrl(settings) + Uri.EscapeDataString(fileName);

    /// <summary>解析 PROPFIND 响应里的 &lt;D:response&gt;，只保留 .json 文件。</summary>
    private static IReadOnlyList<BackupFileInfo> ParseListing(string xml, Uri folderUri)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return Array.Empty<BackupFileInfo>();
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException("WebDAV 返回的目录列表无法解析（可能不是标准 WebDAV 服务器）", ex);
        }

        var result = new List<BackupFileInfo>();
        foreach (var response in document.Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var name = LastPathSegment(href);
            if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prop = response
                .Descendants(Dav + "prop")
                .FirstOrDefault(p => p.Element(Dav + "getcontentlength") is not null
                                     || p.Element(Dav + "getlastmodified") is not null);

            long? size = null;
            var lengthText = prop?.Element(Dav + "getcontentlength")?.Value?.Trim();
            if (long.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
            {
                size = length;
            }

            DateTimeOffset? modified = null;
            var modifiedText = prop?.Element(Dav + "getlastmodified")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(modifiedText)
                && DateTimeOffset.TryParse(
                    modifiedText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                modified = parsed.ToLocalTime();
            }

            var remoteUri = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
                ? absolute
                : new Uri(folderUri, href);

            result.Add(new BackupFileInfo
            {
                FileName = name,
                RemoteUrl = remoteUri.ToString(),
                Size = size,
                LastModified = modified,
            });
        }

        return result
            .OrderByDescending(f => f.LastModified ?? DateTimeOffset.MinValue)
            .ThenByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>取 href 最后一段并 URL 解码（兼容绝对路径与完整 URL）。</summary>
    private static string LastPathSegment(string href)
    {
        var path = href;
        var cut = path.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
        {
            path = path[..cut];
        }

        path = path.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        if (slash < 0)
        {
            return string.Empty;
        }

        return Uri.UnescapeDataString(path[(slash + 1)..]).Trim();
    }

    private static string MapStatus(HttpStatusCode status, string action) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            $"{action}失败（HTTP {(int)status}）：账号或密码不被服务器接受。\n"
            + "• 坚果云必须用【应用密码】，不是登录密码：网页版 → 右上头像 → 账户信息 → 安全选项 → 添加应用 → 生成应用密码；\n"
            + "• 账号要填完整邮箱（如 1324012166@qq.com），不要只填 QQ 号；\n"
            + "• Token/密码复制时别带前后空格（软件已自动去掉首尾空白）。",
        HttpStatusCode.NotFound =>
            $"{action}失败：服务器地址不对（找不到该路径，HTTP 404），请检查 WebDAV 地址",
        HttpStatusCode.Conflict =>
            $"{action}失败（HTTP 409）：父目录不存在或服务器不允许创建目录",
        HttpStatusCode.MethodNotAllowed =>
            $"{action}失败（HTTP 405）：服务器不支持该操作",
        HttpStatusCode.InsufficientStorage =>
            $"{action}失败：服务器存储空间不足（HTTP 507）",
        _ => $"{action}失败（HTTP {(int)status}），请稍后重试",
    };

    private static WebDavCheckResult Fail(string message) => new() { Ok = false, Message = message };

    /// <summary>统一把底层网络异常转换成中文提示（不吞业务异常）。</summary>
    private static async Task<T> GuardAsync<T>(Func<Task<T>> action, string actionName, CancellationToken ct)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"WebDAV {actionName}超时（30 秒无响应），请检查地址与代理设置");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Write($"WebDAV {actionName}失败", ex);
            throw new InvalidOperationException($"无法连接 WebDAV，请检查地址与代理设置（{ex.Message}）", ex);
        }
    }

    private static async Task GuardAsync(Func<Task> action, string actionName, CancellationToken ct)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"WebDAV {actionName}超时（30 秒无响应），请检查地址与代理设置");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Write($"WebDAV {actionName}失败", ex);
            throw new InvalidOperationException($"无法连接 WebDAV，请检查地址与代理设置（{ex.Message}）", ex);
        }
    }
}
