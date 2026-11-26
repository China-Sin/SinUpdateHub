using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// 百度网盘官方开放平台服务（OAuth2 + xpan API）：
/// - 登录：使用用户自行填写的 AppKey / SecretKey + redirect_uri=oob 授权码模式；
/// - 列表与搜索：支持目录浏览、递归枚举子目录与全网盘关键字搜索（Token 过期自动续期重试）；
/// - 下载：filemetas(dlink=1) + User-Agent: pan.baidu.com + HTTP Range 断点续传 + 已存在同大小文件跳过 + 60 秒停滞重试 + 实时网速与 ETA；
/// - 上传：官方三步分片上传（precreate → superfile2 4MB 分片 → create），支持秒传与实时上传进度。
/// </summary>
public sealed class BaiduPanService
{
    public const string DefaultRedirectUri = "oob";
    public const string DefaultAppRootDir = "/";
    public const string BaiduDownloadUserAgent = "pan.baidu.com";
    public const int UploadSliceSizeBytes = 4 * 1024 * 1024; // 百度官方标准 4MB 分片

    private const int BufferSize = 81920;
    private const int ReportBytes = 256 * 1024;
    private const int MaxDownloadAttempts = 3;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

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

    /// <summary>拼接网盘目录与文件名。</summary>
    public static string CombineRemotePath(string dir, string fileName)
    {
        var normalizedDir = NormalizeRemoteDir(dir);
        var cleanName = (fileName ?? string.Empty).Trim().Trim('/');
        return normalizedDir == "/" ? "/" + cleanName : normalizedDir + "/" + cleanName;
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

    /// <summary>计算文件按 4MB 切片后的每片小写 MD5 列表（供 precreate/create 使用）。</summary>
    public static async Task<List<string>> ComputeSliceMd5ListAsync(
        string localFilePath,
        int sliceSize = UploadSliceSizeBytes,
        CancellationToken ct = default)
    {
        var result = new List<string>();
        await using var stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        if (stream.Length == 0)
        {
            result.Add(Convert.ToHexString(MD5.HashData(Array.Empty<byte>())).ToLowerInvariant());
            return result;
        }

        var buffer = new byte[Math.Min(sliceSize, (int)Math.Min(stream.Length, sliceSize))];
        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(sliceSize, stream.Length - stream.Position);
            if (buffer.Length < toRead)
            {
                buffer = new byte[toRead];
            }

            var filled = 0;
            while (filled < toRead)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled, toRead - filled), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                filled += read;
            }

            var hash = MD5.HashData(buffer.AsSpan(0, filled));
            result.Add(Convert.ToHexString(hash).ToLowerInvariant());
        }

        return result;
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
        return await ExecuteWithTokenRefreshAsync(settings, onSettingsUpdated, async token =>
        {
            var url = $"https://pan.baidu.com/rest/2.0/xpan/nas?method=uinfo&access_token={Uri.EscapeDataString(token)}";
            using var resp = await _factory.Client.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<UserInfoDto>(body, JsonConfig.Options)
                      ?? throw new InvalidOperationException("百度网盘用户信息返回为空");

            if (dto.Errno is -6 or 111)
            {
                throw new BaiduTokenExpiredException();
            }

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
        }, ct).ConfigureAwait(false);
    }

    /// <summary>列出指定网盘目录下的全部文件与子文件夹（自动分页 + Token 过期续期重试）。</summary>
    public async Task<List<BaiduFileEntry>> ListFilesAsync(
        AppSettings settings,
        string dir,
        Action? onSettingsUpdated = null,
        CancellationToken ct = default)
    {
        var normalizedDir = NormalizeRemoteDir(dir);
        return await ExecuteWithTokenRefreshAsync(
            settings,
            onSettingsUpdated,
            token => ListFilesCoreAsync(token, normalizedDir, ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>在网盘指定目录下递归搜索文件名包含关键字的文件与文件夹（默认全网盘 / 搜索）。</summary>
    public async Task<List<BaiduFileEntry>> SearchFilesAsync(
        AppSettings settings,
        string keyword,
        string dir = "/",
        Action? onSettingsUpdated = null,
        CancellationToken ct = default)
    {
        var key = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return new List<BaiduFileEntry>();
        }

        var normalizedDir = NormalizeRemoteDir(dir);
        return await ExecuteWithTokenRefreshAsync(settings, onSettingsUpdated, async token =>
        {
            var url = "https://pan.baidu.com/rest/2.0/xpan/file?method=search"
                      + $"&key={Uri.EscapeDataString(key)}"
                      + $"&dir={Uri.EscapeDataString(normalizedDir)}"
                      + "&recursion=1&page=1&num=500&web=0"
                      + $"&access_token={Uri.EscapeDataString(token)}";

            using var resp = await _factory.Client.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<FileListResponseDto>(body, JsonConfig.Options)
                      ?? throw new InvalidOperationException("搜索网盘文件失败：响应为空");

            if (dto.Errno is -6 or 111)
            {
                throw new BaiduTokenExpiredException();
            }

            if (dto.Errno != 0)
            {
                throw new InvalidOperationException(FormatErrno(dto.Errno, dto.ErrMsg));
            }

            return (dto.List ?? new List<FileItemDto>())
                .Select(MapFileEntry)
                .OrderByDescending(f => f.IsDir)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct).ConfigureAwait(false);
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

    /// <summary>批量查询文件下载直链 dlink（每批 100 个，支持 Token 过期自动续期重试）。</summary>
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

        const int batchSize = 100;
        for (var i = 0; i < fsIds.Count; i += batchSize)
        {
            var chunk = fsIds.Skip(i).Take(batchSize).ToList();
            var chunkMap = await ExecuteWithTokenRefreshAsync(settings, onSettingsUpdated, async token =>
            {
                var idArrayJson = "[" + string.Join(",", chunk) + "]";
                var url = "https://pan.baidu.com/rest/2.0/xpan/multimedia?method=filemetas"
                          + $"&fsids={Uri.EscapeDataString(idArrayJson)}"
                          + "&dlink=1"
                          + $"&access_token={Uri.EscapeDataString(token)}";

                using var resp = await _factory.Client.GetAsync(url, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var dto = JsonSerializer.Deserialize<FileMetasResponseDto>(body, JsonConfig.Options)
                          ?? throw new InvalidOperationException("获取百度网盘下载链接失败：响应为空");

                if (dto.Errno is -6 or 111)
                {
                    throw new BaiduTokenExpiredException();
                }

                if (dto.Errno != 0)
                {
                    throw new InvalidOperationException(FormatErrno(dto.Errno, dto.ErrMsg));
                }

                var map = new Dictionary<long, string>();
                if (dto.List is not null)
                {
                    foreach (var item in dto.List)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Dlink))
                        {
                            map[item.FsId] = item.Dlink.Trim();
                        }
                    }
                }

                return map;
            }, ct).ConfigureAwait(false);

            foreach (var kv in chunkMap)
            {
                result[kv.Key] = kv.Value;
            }
        }

        return result;
    }

    /// <summary>构造百度网盘 dlink 下载请求（自动拼接 access_token、附加 User-Agent: pan.baidu.com，支持 HTTP Range 断点续传）。</summary>
    internal static HttpRequestMessage CreateDlinkDownloadRequest(string dlink, string accessToken, long resumeFromBytes = 0)
    {
        var sep = dlink.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var fullUrl = $"{dlink}{sep}access_token={Uri.EscapeDataString(accessToken)}";
        var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
        req.Headers.UserAgent.Clear();
        req.Headers.TryAddWithoutValidation("User-Agent", BaiduDownloadUserAgent);
        if (resumeFromBytes > 0)
        {
            req.Headers.Range = new RangeHeaderValue(resumeFromBytes, null);
        }

        return req;
    }

    /// <summary>
    /// 下载单个百度网盘文件到指定本地路径：
    /// - 若本地目标文件已存在且大小一致则直接跳过；
    /// - 若存在未下完的 .part 临时文件则自动发送 HTTP Range 断点续传；
    /// - 采用无限总时长客户端 + 单次读流 60 秒停滞检测 + 最多 3 次自动断点重试；
    /// - 中途取消或断网会保留 .part 文件以便下次续传。
    /// </summary>
    public async Task<BaiduDownloadFileResult> DownloadFileAsync(
        AppSettings settings,
        string dlink,
        string targetLocalPath,
        long expectedSize = 0,
        IProgress<BaiduTransferProgress>? progress = null,
        Action? onSettingsUpdated = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dlink))
        {
            throw new InvalidOperationException("下载链接为空");
        }

        var fullPath = Path.GetFullPath(targetLocalPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 本地已存在同大小完整文件：直接跳过，避免重复下载数 GB 大文件
        if (expectedSize > 0 && File.Exists(fullPath) && new FileInfo(fullPath).Length == expectedSize)
        {
            progress?.Report(new BaiduTransferProgress
            {
                TransferredBytes = expectedSize,
                TotalBytes = expectedSize,
                Fraction = 1,
                Message = "本地已存在，已跳过",
            });
            return new BaiduDownloadFileResult
            {
                LocalPath = fullPath,
                SkippedExisting = true,
            };
        }

        var partPath = fullPath + ".part";
        var streamClient = _factory.GetClient(Timeout.InfiniteTimeSpan);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var token = await EnsureValidAccessTokenAsync(settings, onSettingsUpdated, forceRefresh: false, ct).ConfigureAwait(false);
            long resumeFrom = 0;
            if (File.Exists(partPath))
            {
                var partLen = new FileInfo(partPath).Length;
                if (partLen > 0 && (expectedSize <= 0 || partLen < expectedSize))
                {
                    resumeFrom = partLen;
                }
                else if (expectedSize > 0 && partLen >= expectedSize)
                {
                    // 异常残余大于目标体积时清掉重下
                    TryDeleteFile(partPath);
                }
            }

            if (attempt > 1)
            {
                progress?.Report(new BaiduTransferProgress
                {
                    TransferredBytes = resumeFrom,
                    TotalBytes = expectedSize > 0 ? expectedSize : null,
                    Fraction = expectedSize > 0 ? Math.Clamp((double)resumeFrom / expectedSize, 0, 1) : 0,
                    IsResumed = resumeFrom > 0,
                    Message = $"网络波动，正在自动断点重试（第 {attempt}/{MaxDownloadAttempts} 次）…",
                });
            }

            try
            {
                using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stallCts.CancelAfter(ConnectTimeout);

                using var req = CreateDlinkDownloadRequest(dlink, token, resumeFrom);
                using var resp = await streamClient
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stallCts.Token)
                    .ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && resumeFrom > 0)
                {
                    // 服务端不支持当前 Range 偏移，删掉 .part 重下
                    TryDeleteFile(partPath);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"百度网盘下载失败（HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}）");
                }

                var isPartial = resp.StatusCode == HttpStatusCode.PartialContent && resumeFrom > 0;
                var startOffset = isPartial ? resumeFrom : 0;
                var fileMode = isPartial ? FileMode.Append : FileMode.Create;

                long? totalBytes = resp.Content.Headers.ContentRange?.Length
                    ?? (resp.Content.Headers.ContentLength is long cl ? startOffset + cl : (expectedSize > 0 ? expectedSize : null));

                var sw = Stopwatch.StartNew();
                var sessionBytes = 0L;
                var received = startOffset;
                var lastReportedBytes = startOffset;
                var lastReportAt = sw.Elapsed;

                await using (var remoteStream = await resp.Content.ReadAsStreamAsync(stallCts.Token).ConfigureAwait(false))
                await using (var localStream = new FileStream(partPath, fileMode, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    var buffer = new byte[BufferSize];
                    while (true)
                    {
                        stallCts.CancelAfter(StallTimeout);
                        var read = await remoteStream.ReadAsync(buffer.AsMemory(0, buffer.Length), stallCts.Token).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        await localStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        received += read;
                        sessionBytes += read;

                        var elapsed = sw.Elapsed;
                        if (received - lastReportedBytes >= ReportBytes || elapsed - lastReportAt >= ReportInterval)
                        {
                            lastReportedBytes = received;
                            lastReportAt = elapsed;
                            var speed = elapsed.TotalSeconds > 0.15 ? sessionBytes / elapsed.TotalSeconds : 0;
                            TimeSpan? eta = totalBytes is > 0 && speed > 1024 && received < totalBytes.Value
                                ? TimeSpan.FromSeconds((totalBytes.Value - received) / speed)
                                : null;

                            progress?.Report(new BaiduTransferProgress
                            {
                                TransferredBytes = received,
                                TotalBytes = totalBytes,
                                Fraction = totalBytes is > 0 ? Math.Clamp((double)received / totalBytes.Value, 0, 1) : 0,
                                BytesPerSecond = speed,
                                EstimatedRemaining = eta,
                                IsResumed = isPartial,
                            });
                        }
                    }

                    progress?.Report(new BaiduTransferProgress
                    {
                        TransferredBytes = received,
                        TotalBytes = totalBytes ?? received,
                        Fraction = 1,
                        BytesPerSecond = sw.Elapsed.TotalSeconds > 0.1 ? sessionBytes / sw.Elapsed.TotalSeconds : 0,
                        EstimatedRemaining = TimeSpan.Zero,
                        IsResumed = isPartial,
                    });
                }

                File.Move(partPath, fullPath, overwrite: true);
                return new BaiduDownloadFileResult
                {
                    LocalPath = fullPath,
                    SkippedExisting = false,
                    ResumedFromBytes = startOffset,
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户主动取消：保留 .part 供下次断点续传
                throw;
            }
            catch (Exception ex) when (attempt < MaxDownloadAttempts && (ex is OperationCanceledException or HttpRequestException or IOException))
            {
                lastError = ex;
                AppLog.Write($"百度网盘下载第 {attempt} 次尝试中断（已保留 .part 准备续传）：{fullPath}", ex);
                await Task.Delay(TimeSpan.FromMilliseconds(1500 * attempt), ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"下载失败（已重试 {MaxDownloadAttempts} 次，未完成进度已保留，再次下载将自动续传）：{lastError?.Message}",
            lastError);
    }

    /// <summary>
    /// 使用百度官方三步分片协议（precreate → superfile2 → create）上传单个本地文件到网盘指定目录。
    /// </summary>
    public async Task<BaiduUploadFileResult> UploadFileAsync(
        AppSettings settings,
        string localFilePath,
        string remoteDir,
        IProgress<BaiduTransferProgress>? progress = null,
        Action? onSettingsUpdated = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
        {
            throw new FileNotFoundException("找不到要上传的本地文件", localFilePath);
        }

        var fileInfo = new FileInfo(localFilePath);
        var fileSize = fileInfo.Length;
        var remotePath = CombineRemotePath(remoteDir, fileInfo.Name);

        progress?.Report(new BaiduTransferProgress
        {
            TransferredBytes = 0,
            TotalBytes = fileSize,
            Fraction = 0,
            Message = "正在计算文件分片校验…",
        });

        var sliceMd5s = await ComputeSliceMd5ListAsync(localFilePath, UploadSliceSizeBytes, ct).ConfigureAwait(false);
        var blockListJson = JsonSerializer.Serialize(sliceMd5s);

        // 1. 预上传 precreate（检测是否秒传并获取 uploadid）
        var precreate = await ExecuteWithTokenRefreshAsync(settings, onSettingsUpdated, async token =>
        {
            var url = $"https://pan.baidu.com/rest/2.0/xpan/file?method=precreate&access_token={Uri.EscapeDataString(token)}";
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["path"] = remotePath,
                ["size"] = fileSize.ToString(),
                ["isdir"] = "0",
                ["autoinit"] = "1",
                ["rtype"] = "1",
                ["block_list"] = blockListJson,
            });
            using var resp = await _factory.Client.PostAsync(url, content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<PrecreateResponseDto>(body, JsonConfig.Options)
                      ?? throw new InvalidOperationException("预上传响应为空");

            if (dto.Errno is -6 or 111)
            {
                throw new BaiduTokenExpiredException();
            }

            if (dto.Errno != 0)
            {
                throw new InvalidOperationException(FormatErrno(dto.Errno, dto.ErrMsg));
            }

            return dto;
        }, ct).ConfigureAwait(false);

        if (precreate.ReturnType == 2)
        {
            progress?.Report(new BaiduTransferProgress
            {
                TransferredBytes = fileSize,
                TotalBytes = fileSize,
                Fraction = 1,
                Message = "云端秒传成功",
            });
            return new BaiduUploadFileResult
            {
                RemotePath = remotePath,
                InstantUploaded = true,
                TotalBytes = fileSize,
            };
        }

        if (string.IsNullOrWhiteSpace(precreate.UploadId))
        {
            throw new InvalidOperationException("预上传未返回 uploadid，无法继续分片上传");
        }

        // 2. 分片上传 superfile2（每片 4MB，multipart/form-data）
        var neededParts = precreate.BlockList is { Count: > 0 }
            ? precreate.BlockList
            : Enumerable.Range(0, sliceMd5s.Count).ToList();

        var uploadClient = _factory.GetClient(TimeSpan.FromMinutes(10));
        var uploadedMd5s = sliceMd5s.ToList();
        var sw = Stopwatch.StartNew();
        long uploadedBytes = 0;

        await using (var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
        {
            foreach (var partSeq in neededParts)
            {
                ct.ThrowIfCancellationRequested();
                var offset = (long)partSeq * UploadSliceSizeBytes;
                var partLen = fileSize == 0 ? 0 : (int)Math.Min(UploadSliceSizeBytes, Math.Max(0, fileSize - offset));
                var sliceBytes = new byte[partLen];

                if (partLen > 0)
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    var filled = 0;
                    while (filled < partLen)
                    {
                        var read = await fs.ReadAsync(sliceBytes.AsMemory(filled, partLen - filled), ct).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        filled += read;
                    }
                }

                var partMd5 = await ExecuteWithTokenRefreshAsync(settings, onSettingsUpdated, async token =>
                {
                    var uploadUrl = "https://d.pcs.baidu.com/rest/2.0/pcs/superfile2?method=upload"
                                    + $"&access_token={Uri.EscapeDataString(token)}"
                                    + "&type=tmpfile"
                                    + $"&path={Uri.EscapeDataString(remotePath)}"
                                    + $"&uploadid={Uri.EscapeDataString(precreate.UploadId)}"
                                    + $"&partseq={partSeq}";

                    using var form = new MultipartFormDataContent();
                    using var fileContent = new ByteArrayContent(sliceBytes);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    form.Add(fileContent, "file", fileInfo.Name);

                    using var resp = await uploadClient.PostAsync(uploadUrl, form, ct).ConfigureAwait(false);
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var dto = JsonSerializer.Deserialize<SuperfileUploadResponseDto>(body, JsonConfig.Options)
                              ?? throw new InvalidOperationException($"分片 {partSeq} 上传响应为空");

                    if (dto.ErrorCode is -6 or 111)
                    {
                        throw new BaiduTokenExpiredException();
                    }

                    if (dto.ErrorCode != 0 || string.IsNullOrWhiteSpace(dto.Md5))
                    {
                        throw new InvalidOperationException(
                            $"分片 {partSeq + 1}/{sliceMd5s.Count} 上传失败：{dto.ErrorMsg ?? $"error_code={dto.ErrorCode}"}");
                    }

                    return dto.Md5.Trim();
                }, ct).ConfigureAwait(false);

                if (partSeq >= 0 && partSeq < uploadedMd5s.Count)
                {
                    uploadedMd5s[partSeq] = partMd5;
                }

                uploadedBytes += partLen;
                var speed = sw.Elapsed.TotalSeconds > 0.1 ? uploadedBytes / sw.Elapsed.TotalSeconds : 0;
                TimeSpan? eta = fileSize > 0 && speed > 1024 && uploadedBytes < fileSize
                    ? TimeSpan.FromSeconds((fileSize - uploadedBytes) / speed)
                    : null;

                progress?.Report(new BaiduTransferProgress
                {
                    TransferredBytes = Math.Min(uploadedBytes, fileSize),
                    TotalBytes = fileSize,
                    Fraction = fileSize > 0 ? Math.Clamp((double)uploadedBytes / fileSize, 0, 1) : 1,
                    BytesPerSecond = speed,
                    EstimatedRemaining = eta,
                });
            }
        }

        // 3. 合并创建文件 create
        var finalBlockListJson = JsonSerializer.Serialize(uploadedMd5s);
        var createdPath = await ExecuteWithTokenRefreshAsync(settings, onSettingsUpdated, async token =>
        {
            var createUrl = $"https://pan.baidu.com/rest/2.0/xpan/file?method=create&access_token={Uri.EscapeDataString(token)}";
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["path"] = remotePath,
                ["size"] = fileSize.ToString(),
                ["isdir"] = "0",
                ["uploadid"] = precreate.UploadId,
                ["block_list"] = finalBlockListJson,
                ["rtype"] = "1",
            });

            using var resp = await _factory.Client.PostAsync(createUrl, content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<CreateFileResponseDto>(body, JsonConfig.Options)
                      ?? throw new InvalidOperationException("合并文件响应为空");

            if (dto.Errno is -6 or 111)
            {
                throw new BaiduTokenExpiredException();
            }

            if (dto.Errno != 0)
            {
                throw new InvalidOperationException(FormatErrno(dto.Errno, dto.ErrMsg));
            }

            return string.IsNullOrWhiteSpace(dto.Path) ? remotePath : dto.Path;
        }, ct).ConfigureAwait(false);

        return new BaiduUploadFileResult
        {
            RemotePath = createdPath,
            InstantUploaded = false,
            TotalBytes = fileSize,
        };
    }

    /// <summary>格式化百度网盘 API 错误码为易懂的中文提示。</summary>
    public static string FormatErrno(int errno, string? fallbackMsg = null) => errno switch
    {
        -6 or 111 => "百度网盘登录已过期或权限不足，请重新登录授权（错误码 " + errno + "）",
        -7 => "文件名或路径格式非法（错误码 -7）",
        -8 => "云端已存在同名文件或目录（错误码 -8）",
        -9 => "网盘目录不存在，请确认路径正确（错误码 -9）",
        2 => "请求参数错误（错误码 2）",
        10 => "创建文件失败，请确认目标目录有写入权限（错误码 10）",
        31034 => "请求过于频繁，触发百度网盘流控，请稍后再试（错误码 31034）",
        31326 => "触发防盗链校验，下载链接已失效，请重新尝试（错误码 31326）",
        _ => string.IsNullOrWhiteSpace(fallbackMsg)
            ? $"百度网盘接口返回错误（errno={errno}）"
            : $"百度网盘接口错误（errno={errno}）：{fallbackMsg}",
    };

    private async Task<T> ExecuteWithTokenRefreshAsync<T>(
        AppSettings settings,
        Action? onSettingsUpdated,
        Func<string, Task<T>> operation,
        CancellationToken ct)
    {
        var token = await EnsureValidAccessTokenAsync(settings, onSettingsUpdated, forceRefresh: false, ct).ConfigureAwait(false);
        try
        {
            return await operation(token).ConfigureAwait(false);
        }
        catch (BaiduTokenExpiredException)
        {
            token = await EnsureValidAccessTokenAsync(settings, onSettingsUpdated, forceRefresh: true, ct).ConfigureAwait(false);
            return await operation(token).ConfigureAwait(false);
        }
    }

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
                result.Add(MapFileEntry(item));
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

    private static BaiduFileEntry MapFileEntry(FileItemDto item) => new()
    {
        FsId = item.FsId,
        Path = item.Path ?? string.Empty,
        Name = item.ServerFilename ?? string.Empty,
        IsDir = item.IsDir == 1,
        Size = item.Size,
        ModifiedAt = item.ServerMtime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(item.ServerMtime)
            : DateTimeOffset.Now,
    };

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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore cleanup errors
        }
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

    private sealed class PrecreateResponseDto
    {
        [JsonPropertyName("errno")]
        public int Errno { get; set; }

        [JsonPropertyName("errmsg")]
        public string? ErrMsg { get; set; }

        [JsonPropertyName("uploadid")]
        public string? UploadId { get; set; }

        [JsonPropertyName("return_type")]
        public int ReturnType { get; set; }

        [JsonPropertyName("block_list")]
        public List<int>? BlockList { get; set; }
    }

    private sealed class SuperfileUploadResponseDto
    {
        [JsonPropertyName("md5")]
        public string? Md5 { get; set; }

        [JsonPropertyName("error_code")]
        public int ErrorCode { get; set; }

        [JsonPropertyName("error_msg")]
        public string? ErrorMsg { get; set; }
    }

    private sealed class CreateFileResponseDto
    {
        [JsonPropertyName("errno")]
        public int Errno { get; set; }

        [JsonPropertyName("errmsg")]
        public string? ErrMsg { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }
    }
}
