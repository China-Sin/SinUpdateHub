using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// 安装包下载：流式写入 <c>*.part</c> 临时文件，完成后再改名，避免中断留下半截文件被误当安装包。
/// 可靠性设计（防止"卡着不动"）：
/// - 连接阶段最多等 60 秒；
/// - 下载过程中若 60 秒收不到任何数据，判定为停滞并取消本次尝试；
/// - 遇到停滞/网络错误自动重试最多 3 次（进度里会显示"正在重试"）；
/// - 401/403/404 抛 <see cref="DownloadRejectedException"/>，由上层刷新下载地址后重试；
/// - 同名且大小一致的文件直接复用；大小不一致自动加 -1/-2 后缀。
/// </summary>
public sealed class DownloadService : IDownloadService
{
    private const int BufferSize = 81920;
    private const int ReportBytes = 256 * 1024;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly NetworkClientFactory _factory;
    private readonly Func<AppSettings> _settings;

    /// <param name="factory">HTTP 客户端工厂（下载使用长超时的 DownloadClient）。</param>
    /// <param name="settings">设置访问器（读取下载目录配置）。</param>
    public DownloadService(NetworkClientFactory factory, Func<AppSettings> settings)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>下载到缓存目录并返回本地完整路径（内部最多重试 3 次）。</summary>
    public async Task<string> DownloadAsync(
        string url,
        string targetFileName,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("下载地址为空，请先点「检查更新」拿到安装包地址");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"下载地址无效：{url}，请重新检查更新");
        }

        var settings = _settings();
        var directory = AppPaths.DownloadDir(settings.DownloadDir);
        AppPaths.Ensure(directory);
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException($"无法创建下载目录：{directory}，请检查磁盘空间或在设置里更换下载目录");
        }

        var initialFileName = AssetMatcher.Sanitize(
            string.IsNullOrWhiteSpace(targetFileName) ? AssetMatcher.FileNameFromUrl(url) : targetFileName);

        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            progress?.Report(new DownloadProgress
            {
                Fraction = 0,
                Message = attempt == 1 ? "正在连接下载服务器…" : $"网络无响应，正在重试（第 {attempt}/{MaxAttempts} 次）…",
            });

            try
            {
                return await DownloadOnceAsync(uri, directory, initialFileName, progress, ct).ConfigureAwait(false);
            }
            catch (DownloadRejectedException)
            {
                // 地址被拒（签名过期等）：交给上层重新解析新地址，不在下载层重试
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                lastError = ex;
                AppLog.Write($"下载第 {attempt} 次尝试停滞/超时：{uri}", ex);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                AppLog.Write($"下载第 {attempt} 次尝试网络失败：{uri}", ex);
            }
            catch (IOException ex)
            {
                lastError = ex;
                AppLog.Write($"下载第 {attempt} 次尝试写入失败：{uri}", ex);
            }

            if (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500 * attempt), ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"下载失败：网络连接不稳定或无响应（已自动重试 {MaxAttempts} 次）。\n"
            + "请检查代理/网络后重试，或在设置里换一个代理。"
            + (lastError is null ? string.Empty : $"\n（{lastError.Message}）"),
            lastError);
    }

    /// <summary>判断是否为 GitHub Release 资产 API 链接（私密仓库必须携带 Accept: application/octet-stream 与 Bearer Token）。</summary>
    internal static bool IsGitHubReleaseAssetApiUri(Uri uri) =>
        uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Contains("/releases/assets/", StringComparison.OrdinalIgnoreCase);

    /// <summary>构造下载请求（GitHub Release 资产 API 自动附加 Accept: application/octet-stream）。</summary>
    internal static HttpRequestMessage CreateDownloadRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (IsGitHubReleaseAssetApiUri(uri))
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        }

        return request;
    }

    /// <summary>单次下载尝试：连接超时 + 停滞检测 + 流式写盘。</summary>
    private async Task<string> DownloadOnceAsync(
        Uri uri,
        string directory,
        string initialFileName,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        // 这个 CTS 负责"连接超时 / 停滞超时"；每次收到数据都会把 CancelAfter 重置成 StallTimeout
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stallCts.CancelAfter(ConnectTimeout);

        HttpResponseMessage response;
        try
        {
            using var request = CreateDownloadRequest(uri);
            response = await _factory.DownloadClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stallCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new OperationCanceledException("连接下载服务器超时（60 秒无响应）", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var message = DescribeHttpFailure(response.StatusCode);
                var statusCode = (int)response.StatusCode;

                // 401/403/404/410 多为「签名下载链接过期」：抛专用异常，让上层重新解析地址后重试
                if (statusCode is 401 or 403 or 404 or 410)
                {
                    throw new DownloadRejectedException(message, statusCode);
                }

                throw new InvalidOperationException(message);
            }

            var fileName = initialFileName;

            // download.php / getfile.aspx 这类动态下载地址：以服务器 Content-Disposition 里的真实文件名为准
            var serverName = AssetMatcher.FileNameFromContentDisposition(response.Content.Headers.ContentDisposition);
            if (!string.IsNullOrWhiteSpace(serverName))
            {
                var safeServerName = AssetMatcher.Sanitize(serverName!);
                if (AssetMatcher.HasInstallerExtension(safeServerName) || !AssetMatcher.HasInstallerExtension(fileName))
                {
                    fileName = safeServerName;
                }
            }

            var destination = Path.Combine(directory, fileName);
            var total = response.Content.Headers.ContentLength;

            // 同名且大小一致 → 直接复用
            if (File.Exists(destination))
            {
                try
                {
                    var existingLength = new FileInfo(destination).Length;
                    if (existingLength > 0 && (!total.HasValue || existingLength == total.Value))
                    {
                        progress?.Report(new DownloadProgress
                        {
                            Fraction = 1,
                            Received = existingLength,
                            Total = total,
                            Message = "文件已存在，直接复用",
                        });
                        return destination;
                    }
                }
                catch (IOException)
                {
                    // 文件被占用等情况，走重新下载
                }
            }

            if (File.Exists(destination))
            {
                destination = BuildUniquePath(directory, fileName);
            }

            var tempPath = destination + ".part";
            TryDelete(tempPath);

            progress?.Report(new DownloadProgress
            {
                Fraction = 0,
                Received = 0,
                Total = total,
                Message = "开始下载…",
            });

            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(stallCts.Token).ConfigureAwait(false);
                await using var target = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    useAsync: true);

                var buffer = new byte[BufferSize];
                long received = 0;
                var bytesSinceReport = 0;
                var lastReport = Stopwatch.GetTimestamp();

                // 重置成"停滞超时"：60 秒收不到数据就取消
                stallCts.CancelAfter(StallTimeout);

                while (true)
                {
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, stallCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException ex)
                    {
                        throw new OperationCanceledException("下载停滞：超过 60 秒没有收到新数据", ex);
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    bytesSinceReport += read;

                    // 有数据就重置停滞计时
                    stallCts.CancelAfter(StallTimeout);

                    var elapsed = Stopwatch.GetElapsedTime(lastReport);
                    if (elapsed >= ReportInterval || bytesSinceReport >= ReportBytes)
                    {
                        Report(progress, received, total, fileName);
                        bytesSinceReport = 0;
                        lastReport = Stopwatch.GetTimestamp();
                    }
                }

                await target.FlushAsync(ct).ConfigureAwait(false);
                await target.DisposeAsync().ConfigureAwait(false);

                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                File.Move(tempPath, destination);
                Report(progress, received, total ?? received, fileName);
                AppLog.Write($"下载完成：{destination}（{received} 字节）");
                return destination;
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                TryDelete(tempPath);
                AppLog.Write($"下载文件无写权限：{destination}", ex);
                throw new InvalidOperationException($"下载失败：没有权限写入「{directory}」，请更换下载目录或在设置里修改", ex);
            }
            catch (IOException ex)
            {
                TryDelete(tempPath);
                AppLog.Write($"写入下载文件失败：{tempPath}", ex);
                throw new InvalidOperationException($"下载失败：无法写入文件「{destination}」，请检查磁盘空间或文件是否被占用", ex);
            }
        }
    }

    private static void Report(IProgress<DownloadProgress>? progress, long received, long? total, string fileName)
    {
        if (progress is null)
        {
            return;
        }

        var fraction = total is > 0 ? Math.Clamp((double)received / total.Value, 0d, 1d) : -1d;
        var message = total is > 0
            ? $"{fileName}  {FormatSize(received)} / {FormatSize(total.Value)}"
            : $"{fileName}  {FormatSize(received)}";
        progress.Report(new DownloadProgress
        {
            Fraction = fraction < 0 ? 0 : fraction,
            Received = received,
            Total = total,
            Message = message,
        });
    }

    private static string DescribeHttpFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound =>
            "下载失败（HTTP 404）：地址可能已失效，请重新检查更新拿最新地址",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "下载失败（HTTP 401/403）：下载地址需要登录或已过期，请重新检查更新",
        HttpStatusCode.TooManyRequests =>
            "下载失败（HTTP 429）：请求过于频繁，请稍等几分钟再试",
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
            $"下载失败（HTTP {(int)status}）：下载服务器暂时不可用，请稍后重试",
        _ => $"下载失败（HTTP {(int)status}）：请检查网络或代理设置后重试",
    };

    private static string BuildUniquePath(string directory, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name}-{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / (1024d * 1024):0.0} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
    }

    private static void TryDelete(string path)
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
            // 清理失败不影响主流程
        }
    }
}
