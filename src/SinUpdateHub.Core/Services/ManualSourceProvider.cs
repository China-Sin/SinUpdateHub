using System.Net;
using System.Net.Http;
using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// 「软件下载（官网/网盘/手动）」来源：没有标准自动版本接口；
/// 检查时验证网页链接格式并探测可达性（HTTP HEAD/GET），链接无法访问或报错时抛出异常以标记为「检查失败」；
/// 「打开网页」时用系统浏览器打开对应下载页。
/// </summary>
public sealed class ManualSourceProvider : IUpdateSourceProvider
{
    private readonly NetworkClientFactory? _factory;

    public ManualSourceProvider(NetworkClientFactory? factory = null)
    {
        _factory = factory;
    }

    public SourceKind Kind => SourceKind.Custom;

    public async Task<AvailableUpdate> GetLatestAsync(AppEntry entry, CancellationToken ct = default)
    {
        var rawUrl = !string.IsNullOrWhiteSpace(entry.HomepageUrl)
            ? entry.HomepageUrl
            : entry.ReleasePageUrl;
        var pageUrl = (rawUrl ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(pageUrl))
        {
            throw new UpdateSourceException("网页链接为空，请点「编辑」填写下载地址");
        }

        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new UpdateSourceException($"网页链接格式无效：{pageUrl}");
        }

        if (_factory is not null)
        {
            await ProbeUrlAsync(uri, ct).ConfigureAwait(false);
        }

        return new AvailableUpdate
        {
            Version = string.Empty,
            DownloadUrl = null,
            ReleasePageUrl = pageUrl,
            HomepageUrl = pageUrl,
            Advisory = "手动下载来源：点「打开网页」打开下载地址。",
        };
    }

    private async Task ProbeUrlAsync(Uri uri, CancellationToken ct)
    {
        var client = _factory!.GetClient(TimeSpan.FromSeconds(10));
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, uri);
            using var headResponse = await client
                .SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (headResponse.StatusCode is HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.BadRequest
                or HttpStatusCode.NotImplemented)
            {
                await ProbeWithGetAsync(client, uri, ct).ConfigureAwait(false);
                return;
            }

            ValidateStatusCode(headResponse.StatusCode);
        }
        catch (UpdateSourceException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new UpdateSourceException("网页连接超时，无法访问", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateSourceException($"网页无法访问：{ex.Message}", ex);
        }
    }

    private static async Task ProbeWithGetAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        using var getResponse = await client
            .SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        ValidateStatusCode(getResponse.StatusCode);
    }

    private static void ValidateStatusCode(HttpStatusCode status)
    {
        var code = (int)status;
        if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone || code >= 500)
        {
            throw new UpdateSourceException($"网页无法访问（HTTP {code}）");
        }
    }
}
