using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// 百度网盘官方开放平台服务（OAuth2 + xpan API）：
/// - 登录：使用用户自行填写的 AppKey / SecretKey + redirect_uri=oob 授权码模式；
/// - 列表：支持浏览指定网盘目录（默认 /apps/SinUpdateHub）与递归枚举子目录；
/// - 下载：通过 filemetas(dlink=1) 获取直链，请求时按官方要求携带 User-Agent: pan.baidu.com 与 access_token，
///         流式写入 .part 临时文件后再原子改名，支持取消与自动刷新过期 Token。
/// </summary>
public sealed class BaiduPanService
{
    public const string DefaultRedirectUri = "oob";
    public const string DefaultAppRootDir = "/";
    public const string BaiduDownloadUserAgent = "pan.baidu.com";

    private const int BufferSize = 81920;
    private const int ReportBytes = 256 * 1024;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(200);

    private readonly NetworkClientFactory _factory;

    public BaiduPanService(NetworkClientFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>构造百度网盘 OAuth2 授权登录页地址。</summary>
    public static string BuildAuthorizeUrl(string appKey)
    {
        var key = (appKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("请先填写百度网盘开放平台的 AppKey");
        }

        return "https://openapi.baidu.com/oauth/2.0/authorize"
               + "?response_type=code"
               + $"&client_id={Uri.EscapeDataString(key)}"
               + $"&redirect_uri={Uri.EscapeDataString(DefaultRedirectUri)}"
               + "&scope=basic,netdisk"
               + "&display=popup";
    }

    /// <summary>从百度 OAuth2 回调地址中提取授权码 code（支持 query 与 fragment 两种形式）。</summary>
    public static string? TryExtractAuthCode(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        var fromQuery = ExtractQueryParam(uri.Query, "code");
        if (!string.IsNullOrWhiteSpace(fromQuery))
        {
            return fromQuery;
        }

        if (!string.IsNullOrWhiteSpace(uri.Fragment))
        {
            var frag = uri.Fragment.TrimStart('#');
            var fromFrag = ExtractQueryParam(frag, "code");
            if (!string.IsNullOrWhiteSpace(fromFrag))
            {
                return fromFrag;
            }
        }

        return null;
    }

    /// <summary>规范化网盘目录路径（确保以 / 开头且不以多余 / 结尾）。</summary>
    public static string NormalizeRemoteDir(string? dir)
    {
        var raw = (dir ?? string.Empty).Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultAppRootDir;
        }

        while (raw.Contains("//", StringComparison.Ordinal))
        {
            raw = raw.Replace("//", "/", StringComparison.Ordinal);
        }

        if (!raw.StartsWith('/'))
        {
            raw = "/" + raw;
        }

        if (raw.Length > 1 && raw.EndsWith('/'))
        {
            raw = raw.TrimEnd('/');
        }

        return raw;
    }

    /// <summary>获取上级目录路径。</summary>
    public static string GetParentDir(string? dir)
    {
        var normalized = NormalizeRemoteDir(dir);
        if (normalized == "/")
        {
            return "/";
        }

        var idx = normalized.LastIndexOf('/');
        return idx <= 0 ? "/" : normalized[..idx];
    }

    /// <summary>用 OAuth2 授权码 code 换取 access_token 与 refresh_token。</summary>
    public async Task<BaiduTokenResult> ExchangeCodeAsync(
        string appKey,
        string secretKey,
        string code,
        CancellationToken ct = default)
    {
        var key = (appKey ?? string.Empty).Trim();
        var secret = (secretKey ?? string.Empty).Trim();
        var authCode = (code ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("请先完整填写 AppKey 与 SecretKey");
        }

        if (string.IsNullOrWhiteSpace(authCode))
        {
            throw new InvalidOperationException("未获取到百度授权码（code）");
        }

        var url = "https://openapi.baidu.com/oauth/2.0/token"
                  + "?grant_type=authorization_code"
                  + $"&code={Uri.EscapeDataString(authCode)}"
                  + $"&client_id={Uri.EscapeDataString(key)}"
                  + $"&client_secret={Uri.EscapeDataString(secret)}"
                  + $"&redirect_uri={Uri.EscapeDataString(DefaultRedirectUri)}";

        return await RequestTokenAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>使用 refresh_token 刷新 access_token。</summary>
    public async Task<BaiduTokenResult> RefreshTokenAsync(
        string appKey,
        string secretKey,
        string refreshToken,
        CancellationToken ct = default)
    {
        var key = (appKey ?? string.Empty).Trim();
        var secret = (secretKey ?? string.Empty).Trim();
        var refresh = (refreshToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(refresh))
        {
            throw new InvalidOperationException("百度网盘登录已失效，请重新登录");
        }

        var url = "https://openapi.baidu.com/oauth/2.0/token"
                  + "?grant_type=refresh_token"
                  + $"&refresh_token={Uri.EscapeDataString(refresh)}"
                  + $"&client_id={Uri.EscapeDataString(key)}"
                  + $"&client_secret={Uri.EscapeDataString(secret)}";

        return await RequestTokenAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>确保 access_token 有效；若快过期且存在 refresh_token 则自动刷新并回写 settings。</summary>
    public async Task<string> EnsureValidAccessTokenAsync(
        AppSettings settings,
        Action? onSettingsUpdated = null,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var hasAccess = !string.IsNullOrWhiteSpace(settings.BaiduAccessToken);
        var needRefresh = forceRefresh
                          || !hasAccess
                          || (settings.BaiduTokenExpiresAt is not null
                              && settings.BaiduTokenExpiresAt.Value <= DateTimeOffset.Now.AddMinutes(10));

        if (!needRefresh)
        {
            return settings.BaiduAccessToken.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.BaiduRefreshToken)
            && !string.IsNullOrWhiteSpace(settings.BaiduAppKey)
            && !string.IsNullOrWhiteSpace(settings.BaiduSecretKey))
        {
            var refreshed = await RefreshTokenAsync(
                settings.BaiduAppKey,
                settings.BaiduSecretKey,
                settings.BaiduRefreshToken,
                ct).ConfigureAwait(false);
            settings.BaiduAccessToken = refreshed.AccessToken;
            if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
            {
                settings.BaiduRefreshToken = refreshed.RefreshToken;
            }

            settings.BaiduTokenExpiresAt = refreshed.ExpiresAt;
            onSettingsUpdated?.Invoke();
            return settings.BaiduAccessToken;
        }

        if (hasAccess && !forceRefresh)
        {
            return settings.BaiduAccessToken.Trim();
        }

        throw new InvalidOperationException("百度网盘未登录或登录已过期，请点击「登录百度网盘」重新授权");
    }

    /// <summary>获取当前登录用户信息。</summary>
    public async Task<BaiduUserInfo> GetUserInfoAsync(
        AppSettings settings,
        Action? onSettingsUpdated = null,
        CancellationToken ct = default)
    {
        var token = await EnsureValidAccessTokenAsync(settings, onSettingsUpdated, forceRefresh: false, ct).ConfigureAwait(false);
        var url = $"https://pan.baidu.com/rest/2.0/xpan/nas?method=uinfo&access_token={Uri.EscapeDataString(token)}";
        using var resp = await _factory.Client.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<UserInfoDto>(body, JsonConfig.Options)
                  ?? throw new InvalidOperationException("百度网盘用户信息返回为空");

        if (dto.Errno != 0)
        {
            throw new InvalidOperationException(FormatErrno(dto.Errno, dto.ErrMsg));
        }

        return new BaiduUserInfo
        {
            Uk = dto.Uk,
            BaiduName = dto.BaiduName ?? string.Empty,
            NetdiskName = dto.NetdiskName ?? string.Empty,
            VipType = dto.VipType,
        };
    }

    /// <summary>列出指定网盘目录下的全部文件与子文件夹（自动分页）。</summary>
    public async Task<List<BaiduFileEntry>> ListFilesAsync(
        AppSettings settings,
        string dir,
        Action? onSettingsUpdated = null,
        CancellationToken ct = default)
    {
        var normalizedDir = NormalizeRemoteDir(dir);
        var token = await EnsureValidAccessTokenAsync(settings, onSettingsUpdated, forceRefresh: false, ct).ConfigureAwait(false);

        try
        {
            return await ListFilesCoreAsync(token, normalizedDir, ct).ConfigureAwait(false);
        }
        catch (BaiduTokenExpiredException)
        {
            token = await EnsureValidAccessTokenAsync(settings, onSettingsUpdated, forceRefresh: true, ct).ConfigureAwait(false);
            return await ListFilesCoreAsync(token, normalizedDir, ct).ConfigureAwait(false);
        }
    }

    /// <summary>递归收集选中文件与文件夹内的所有文件（保留相对子目录结构）。</summary>
    public async Task<List<BaiduDownloadTaskItem>> CollectFilesRecursiveAsync(
        AppSettings settings,
        IEnumerable<BaiduFileEntry> selectedItems,
        string currentDir,
        Action? onSettingsUpdated = null,
        CancellationToken ct = default)
    {
        var results = new List<BaiduDownloadTaskItem>();
        var baseDir = NormalizeRemoteDir(currentDir);

        async Task WalkFolderAsync(string folderPath, string relativePrefix)
        {
            ct.ThrowIfCancellationRequested();
            var children = await ListFilesAsync(settings, folderPath, onSettingsUpdated, ct).ConfigureAwait(false);
            foreach (var child in children)
            {
                var safeName = AssetMatcher.Sanitize(child.Name);
                var relPath = string.IsNullOrEmpty(relativePrefix)
                    ? safeName
                    : Path.Combine(relativePrefix, safeName);

                if (child.IsDir)
                {
                    await WalkFolderAsync(child.Path, relPath).ConfigureAwait(false);
                }
                else
                {
                    results.Add(new BaiduDownloadTaskItem
                    {
                        File = child,
                        RelativeLocalPath = relPath,
                    });
                }
            }
        }

        foreach (var item in selectedItems)
        {
            ct.ThrowIfCancellationRequested();
            var safeName = AssetMatcher.Sanitize(item.Name);
            if (item.IsDir)
            {
                await WalkFolderAsync(item.Path, safeName).ConfigureAwait(false);
            }
            else
            {
                var rel = ComputeRelativePath(baseDir, item.Path, safeName);
                results.Add(new BaiduDownloadTaskItem
                {
                    File = item,
                    RelativeLocalPath = rel,
                });
            }
        }

        return results;
    }

    /// <summary>批量查询文件下载直链 dlink（自动按每批 100 个切分）。</summary>
    public async Task<Dictionary<long, string>> GetDownloadLinksAsync(
        AppSettings settings,
        IReadOnlyList<long> fsIds,
        Action? onSettingsUpdated = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<long, string>();
        if (fsIds.Count == 0)
        {
            return result;
        }

        var token = await EnsureValidAccessTokenAsync(settings, onSettingsUpdated, forceRefresh: false, ct).ConfigureAwait(false);
        const int batchSize = 100;
        for (var i = 0; i < fsIds.Count; i += batchSize)
        {
            var chunk = fsIds.Skip(i).Take(batchSize).ToList();
            var idArrayJson = "[" + string.Join(",", chunk) + "]";
            var url = "https://pan.baidu.com/rest/2.0/xpan/multimedia?method=filemetas"
                      + $"&fsids={Uri.EscapeDataString(idArrayJson)}"
                      + "&dlink=1"
                      + $"&access_token={Uri.EscapeDataString(token)}";

            using var resp = await _factory.Client.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<FileMetasResponseDto>(body, JsonConfig.Options)
                      ?? throw new InvalidOperationException("获取百度网盘下载链接失败：响应为空");

            if (dto.Errno != 0)
            {
                throw new InvalidOperationException(FormatErrno(dto.Errno, dto.ErrMsg));
            }

            if (dto.List is null)
            {
                continue;
            }

            foreach (var item in dto.List)
            {
                if (!string.IsNullOrWhiteSpace(item.Dlink))
                {
                    result[item.FsId] = item.Dlink.Trim();
                }
            }
        }

        return result;
    }

    /// <summary>构造百度网盘 dlink 下载请求（自动拼接 access_token 并附加官方要求的 User-Agent: pan.baidu.com）。</summary>
    internal static HttpRequestMessage CreateDlinkDownloadRequest(string dlink, string accessToken)
    {
        var sep = dlink.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var fullUrl = $"{dlink}{sep}access_token={Uri.EscapeDataString(accessToken)}";
        var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
        req.Headers.UserAgent.Clear();
        req.Headers.TryAddWithoutValidation("User-Agent", BaiduDownloadUserAgent);
        return req;
    }

    /// <summary>下载单个百度网盘文件到指定本地路径（.part 临时文件 + 原子改名）。</summary>
    public async Task<string> DownloadFileAsync(
        AppSettings settings,
        string dlink,
        string targetLocalPath,
        IProgress<DownloadProgress>? progress = null,
        Action? onSettingsUpdated = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dlink))
        {
            throw new InvalidOperationException("下载链接为空");
        }

        var token = await EnsureValidAccessTokenAsync(settings, onSettingsUpdated, forceRefresh: false, ct).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(targetLocalPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var partPath = fullPath + ".part";
        using var req = CreateDlinkDownloadRequest(dlink, token);
        using var resp = await _factory.DownloadClient
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"百度网盘下载失败（HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}）");
        }

        var totalBytes = resp.Content.Headers.ContentLength;
        try
        {
            await using (var remoteStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var localStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                long received = 0;
                long lastReportedBytes = 0;
                var lastReportAt = DateTimeOffset.UtcNow;

                while (true)
                {
                    var read = await remoteStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    await localStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;

                    var now = DateTimeOffset.UtcNow;
                    if (received - lastReportedBytes >= ReportBytes || now - lastReportAt >= ReportInterval)
                    {
                        lastReportedBytes = received;
                        lastReportAt = now;
                        progress?.Report(new DownloadProgress
                        {
                            Received = received,
                            Total = totalBytes,
                            Fraction = totalBytes is > 0 ? Math.Clamp((double)received / totalBytes.Value, 0, 1) : 0,
                        });
                    }
                }

                progress?.Report(new DownloadProgress
                {
                    Received = received,
                    Total = totalBytes ?? received,
                    Fraction = 1,
                });
            }

            File.Move(partPath, fullPath, overwrite: true);
            return fullPath;
        }
        catch
        {
            try
            {
                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                }
            }
            catch
            {
                // ignore cleanup failure
            }

            throw;
        }
    }

    /// <summary>格式化百度网盘 API 错误码为易懂的中文提示。</summary>
    public static string FormatErrno(int errno, string? fallbackMsg = null) => errno switch
    {
        -6 or 111 => "百度网盘登录已过期或权限不足，请重新登录授权（错误码 " + errno + "）",
        -7 => "文件名或路径格式非法（错误码 -7）",
        -9 => "网盘目录不存在（个人应用默认仅可访问 /apps/<你的应用名>/ 目录，请确认路径已存在）",
        2 => "请求参数错误（错误码 2）",
        31034 => "请求过于频繁，触发百度网盘流控，请稍后再试（错误码 31034）",
        31326 => "触发防盗链校验，下载链接已失效，请重新尝试（错误码 31326）",
        _ => string.IsNullOrWhiteSpace(fallbackMsg)
            ? $"百度网盘接口返回错误（errno={errno}）"
            : $"百度网盘接口错误（errno={errno}）：{fallbackMsg}",
    };

    private async Task<BaiduTokenResult> RequestTokenAsync(string url, CancellationToken ct)
    {
        using var resp = await _factory.Client.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<TokenResponseDto>(body, JsonConfig.Options)
                  ?? throw new InvalidOperationException("百度授权接口返回为空");

        if (!string.IsNullOrWhiteSpace(dto.Error) || string.IsNullOrWhiteSpace(dto.AccessToken))
        {
            var desc = string.IsNullOrWhiteSpace(dto.ErrorDescription) ? dto.Error : dto.ErrorDescription;
            throw new InvalidOperationException($"百度授权失败：{desc ?? "请核对 AppKey 与 SecretKey"}");
        }

        var expiresIn = dto.ExpiresIn > 0 ? dto.ExpiresIn : 2592000;
        return new BaiduTokenResult
        {
            AccessToken = dto.AccessToken.Trim(),
            RefreshToken = (dto.RefreshToken ?? string.Empty).Trim(),
            ExpiresInSeconds = expiresIn,
            ExpiresAt = DateTimeOffset.Now.AddSeconds(expiresIn),
            Scope = dto.Scope ?? string.Empty,
        };
    }

    private async Task<List<BaiduFileEntry>> ListFilesCoreAsync(string token, string normalizedDir, CancellationToken ct)
    {
        var result = new List<BaiduFileEntry>();
        var start = 0;
        const int limit = 1000;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var url = "https://pan.baidu.com/rest/2.0/xpan/file?method=list"
                      + $"&dir={Uri.EscapeDataString(normalizedDir)}"
                      + "&order=name&desc=0"
                      + $"&start={start}&limit={limit}"
                      + "&web=0&folder=0"
                      + $"&access_token={Uri.EscapeDataString(token)}";

            using var resp = await _factory.Client.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<FileListResponseDto>(body, JsonConfig.Options)
                      ?? throw new InvalidOperationException("读取网盘目录失败：响应为空");

            if (dto.Errno is -6 or 111)
            {
                throw new BaiduTokenExpiredException();
            }

            if (dto.Errno != 0)
            {
                throw new InvalidOperationException(FormatErrno(dto.Errno, dto.ErrMsg));
            }

            var page = dto.List;
            if (page is null || page.Count == 0)
            {
                break;
            }

            foreach (var item in page)
            {
                result.Add(new BaiduFileEntry
                {
                    FsId = item.FsId,
                    Path = item.Path ?? string.Empty,
                    Name = item.ServerFilename ?? string.Empty,
                    IsDir = item.IsDir == 1,
                    Size = item.Size,
                    ModifiedAt = item.ServerMtime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(item.ServerMtime)
                        : DateTimeOffset.Now,
                });
            }

            if (page.Count < limit)
            {
                break;
            }

            start += page.Count;
        }

        // 文件夹排在前面，其次按名称排序
        return result
            .OrderByDescending(f => f.IsDir)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ComputeRelativePath(string baseDir, string remotePath, string fallbackFileName)
    {
        var normalized = NormalizeRemoteDir(remotePath);
        var prefix = baseDir == "/" ? "/" : baseDir + "/";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var rel = normalized[prefix.Length..];
            var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(AssetMatcher.Sanitize)
                .ToArray();
            if (parts.Length > 0)
            {
                return Path.Combine(parts);
            }
        }

        return fallbackFileName;
    }

    private static string? ExtractQueryParam(string queryOrFragment, string key)
    {
        if (string.IsNullOrWhiteSpace(queryOrFragment))
        {
            return null;
        }

        var trimmed = queryOrFragment.TrimStart('?', '#');
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && string.Equals(kv[0], key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(kv[1]);
            }
        }

        return null;
    }

    private sealed class BaiduTokenExpiredException : Exception
    {
    }

    private sealed class TokenResponseDto
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }

    private sealed class UserInfoDto
    {
        [JsonPropertyName("errno")]
        public int Errno { get; set; }

        [JsonPropertyName("errmsg")]
        public string? ErrMsg { get; set; }

        [JsonPropertyName("uk")]
        public long Uk { get; set; }

        [JsonPropertyName("baidu_name")]
        public string? BaiduName { get; set; }

        [JsonPropertyName("netdisk_name")]
        public string? NetdiskName { get; set; }

        [JsonPropertyName("vip_type")]
        public int VipType { get; set; }
    }

    private sealed class FileListResponseDto
    {
        [JsonPropertyName("errno")]
        public int Errno { get; set; }

        [JsonPropertyName("errmsg")]
        public string? ErrMsg { get; set; }

        [JsonPropertyName("list")]
        public List<FileItemDto>? List { get; set; }
    }

    private sealed class FileItemDto
    {
        [JsonPropertyName("fs_id")]
        public long FsId { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("server_filename")]
        public string? ServerFilename { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("isdir")]
        public int IsDir { get; set; }

        [JsonPropertyName("server_mtime")]
        public long ServerMtime { get; set; }
    }

    private sealed class FileMetasResponseDto
    {
        [JsonPropertyName("errno")]
        public int Errno { get; set; }

        [JsonPropertyName("errmsg")]
        public string? ErrMsg { get; set; }

        [JsonPropertyName("list")]
        public List<FileMetaItemDto>? List { get; set; }
    }

    private sealed class FileMetaItemDto
    {
        [JsonPropertyName("fs_id")]
        public long FsId { get; set; }

        [JsonPropertyName("dlink")]
        public string? Dlink { get; set; }
    }
}
