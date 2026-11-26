using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// GitHub Releases 来源实现。
/// 读取 <see cref="AppEntry.RepoUrl"/> 指向仓库的最新 Release，用 <see cref="AppEntry.Package"/>
/// 过滤 Assets（普通"包含"或正则）并自动挑选最合适的安装包。
/// 结果缓存 5 分钟，避免"检查全部"时同一仓库重复请求触发 GitHub API 限流；
/// 网络请求统一走 <see cref="NetworkClientFactory.Client"/>（自动带系统代理 / Token / User-Agent）。
/// </summary>
public sealed class GitHubSourceProvider : IUpdateSourceProvider
{
    /// <summary>同一仓库同一筛选条件的结果缓存时长。</summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>发布说明最多保留的字符数（避免界面被超长正文撑爆）。</summary>
    private const int ReleaseNotesLimit = 2000;

    /// <summary>"未匹配到安装包"提示里最多列出的资产数量。</summary>
    private const int MaxListedAssets = 10;

    private readonly NetworkClientFactory _factory;
    private readonly Func<AppSettings> _settings;
    private readonly ConcurrentDictionary<string, CacheItem> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="factory">HTTP 客户端工厂（负责代理 / Token / User-Agent）。</param>
    /// <param name="settings">设置访问器（检查时读取"包含预发布版本"开关）。</param>
    public GitHubSourceProvider(NetworkClientFactory factory, Func<AppSettings> settings)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.GitHub;

    /// <summary>
    /// 查询最新版本：优先使用 <c>releases/latest</c>；开启"包含预发布"或 latest 返回 404 时，
    /// 退回到 <c>releases?per_page=20</c> 列表里按规则挑选。
    /// </summary>
    public async Task<AvailableUpdate> GetLatestAsync(AppEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var repo = AssetMatcher.ParseGitHubRepo(entry.RepoUrl);
        if (repo is null)
        {
            throw new UpdateSourceException("GitHub 地址无法解析，请填 https://github.com/owner/repo");
        }

        var owner = repo.Value.Owner;
        var repoName = repo.Value.Repo;
        var settings = _settings();
        var includePrerelease = settings.IncludePrerelease;
        var hasToken = !string.IsNullOrWhiteSpace(settings.GitHubToken);
        var cacheKey = string.Join(
            '|',
            owner + "/" + repoName,
            includePrerelease,
            hasToken,
            entry.Package ?? string.Empty,
            entry.PackageIsRegex);

        if (TryGetCached(cacheKey, out var cached))
        {
            return CloneUpdate(cached);
        }

        var client = _factory.Client;
        var release = await FetchReleaseAsync(client, owner, repoName, includePrerelease, ct).ConfigureAwait(false);

        var version = VersionComparer.Normalize(release.TagName);
        if (string.IsNullOrWhiteSpace(version))
        {
            version = (release.TagName ?? string.Empty).Trim();
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new UpdateSourceException("无法从 Release 标签解析版本号，请确认该仓库的最新发布带有 tag");
        }

        // 已配置 GitHub Token 时优先使用资产 API URL（兼容私密仓库与公开仓库，下载时携带 Bearer Token + Accept: application/octet-stream）
        var assets = (release.Assets ?? new List<AssetDto>())
            .Where(a => !string.IsNullOrWhiteSpace(a.Name)
                && (!string.IsNullOrWhiteSpace(a.BrowserDownloadUrl) || !string.IsNullOrWhiteSpace(a.ApiUrl)))
            .Select(a =>
            {
                var downloadUrl = hasToken && !string.IsNullOrWhiteSpace(a.ApiUrl)
                    ? a.ApiUrl!.Trim()
                    : (a.BrowserDownloadUrl ?? a.ApiUrl)!.Trim();
                return new AssetCandidate
                {
                    Name = a.Name!.Trim(),
                    Url = downloadUrl,
                    Size = a.Size,
                    ContentType = a.ContentType,
                };
            })
            .ToList();

        var filtered = AssetMatcher.Filter(assets, entry.Package, entry.PackageIsRegex);
        if (filtered.Count == 0)
        {
            var available = assets.Count == 0
                ? "（该 Release 没有任何 Assets）"
                : string.Join("、", assets.Take(MaxListedAssets).Select(a => a.Name))
                  + (assets.Count > MaxListedAssets ? " 等" : string.Empty);
            throw new UpdateSourceException($"未匹配到安装包。可用资产：{available}");
        }

        var best = AssetMatcher.PickBest(filtered);
        var update = new AvailableUpdate
        {
            Version = version,
            DownloadUrl = best?.Url,
            AssetName = best?.Name,
            AssetSize = best?.Size,
            ReleasePageUrl = release.HtmlUrl,
            ReleaseNotes = Truncate(release.Body, ReleaseNotesLimit),
            PublishedAt = release.PublishedAt,
            Candidates = filtered,
        };

        if (best is null)
        {
            update.Advisory = $"已匹配到 {filtered.Count} 个文件，但都不像可自动安装的包，请在候选文件里手动选择";
        }

        _cache[cacheKey] = new CacheItem(DateTimeOffset.Now, update);
        AppLog.Write($"GitHub 检查成功：{owner}/{repoName} → {version}（选中：{best?.Name ?? "无"}）");
        return CloneUpdate(update);
    }

    /// <summary>清空结果缓存（设置变更或用户手动刷新时调用）。</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>拉取 Release：latest 优先，开启预发布或 latest 404 时走列表接口。</summary>
    private static async Task<ReleaseDto> FetchReleaseAsync(
        HttpClient client,
        string owner,
        string repoName,
        bool includePrerelease,
        CancellationToken ct)
    {
        if (!includePrerelease)
        {
            var latestUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repoName)}/releases/latest";
            using var latestResponse = await client.GetAsync(latestUrl, ct).ConfigureAwait(false);
            if (latestResponse.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(latestResponse, ct).ConfigureAwait(false);
                return await DeserializeAsync<ReleaseDto>(latestResponse, ct).ConfigureAwait(false)
                       ?? throw new UpdateSourceException("GitHub 返回内容无法解析，请稍后重试");
            }

            // latest 404：可能是仓库只有预发布版本，退回到列表接口继续尝试
            AppLog.Write($"GitHub latest 返回 404，改用列表接口：{owner}/{repoName}");
        }

        var listUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repoName)}/releases?per_page=20";
        using var listResponse = await client.GetAsync(listUrl, ct).ConfigureAwait(false);
        if (listResponse.StatusCode == HttpStatusCode.NotFound)
        {
            throw new UpdateSourceException("该仓库不存在或没有发布（Releases）");
        }

        await EnsureSuccessAsync(listResponse, ct).ConfigureAwait(false);
        var releases = await DeserializeAsync<List<ReleaseDto>>(listResponse, ct).ConfigureAwait(false);
        if (releases is null || releases.Count == 0)
        {
            throw new UpdateSourceException("该仓库不存在或没有发布（Releases）");
        }

        if (includePrerelease)
        {
            // GitHub 列表按发布时间倒序，第一条即最新（含预发布）
            return releases[0];
        }

        var stable = releases.FirstOrDefault(r => !r.Prerelease && !r.Draft);
        if (stable is null)
        {
            throw new UpdateSourceException("该仓库只有预发布版本，可在设置里开启「检查时包含预发布版本」后再检查");
        }

        return stable;
    }

    /// <summary>非成功状态统一转成中文可操作提示（含限流恢复时间）。</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var code = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == (HttpStatusCode)429)
        {
            throw new UpdateSourceException(DescribeForbidden(response));
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new UpdateSourceException("该仓库不存在或没有发布（Releases）");
        }

        var snippet = await ReadSnippetAsync(response, ct).ConfigureAwait(false);
        AppLog.Write($"GitHub API 返回 HTTP {code}：{snippet}");
        throw new UpdateSourceException(
            $"GitHub 请求失败（HTTP {code}）。请检查仓库地址、网络或代理设置。{snippet}".TrimEnd('。', ' '));
    }

    /// <summary>区分"限流"与"私有仓库/被拒绝"两种 403，并给出恢复时间。</summary>
    private static string DescribeForbidden(HttpResponseMessage response)
    {
        var remaining = ReadHeaderLong(response, "X-RateLimit-Remaining");
        var reset = ReadHeaderLong(response, "X-RateLimit-Reset");

        // 主限流会把剩余次数打到 0；私有仓库 403 的剩余次数通常仍大于 0，可按"被拒绝"提示。
        var isRateLimited = response.StatusCode == (HttpStatusCode)429 || remaining is 0;
        if (!isRateLimited)
        {
            return "GitHub 拒绝访问（HTTP 403）：仓库可能是私有的，或当前网络被 GitHub 限制，请稍后再试";
        }

        if (reset is long seconds)
        {
            var resetAt = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
            var minutes = Math.Max(1, (int)Math.Ceiling((resetAt - DateTimeOffset.Now).TotalMinutes));
            return $"GitHub API 限流，恢复时间 {resetAt:HH:mm}（约 {minutes} 分钟后）。可在设置里填 GitHub Token 提升到 5000 次/小时";
        }

        return "GitHub API 限流，可在设置里填 GitHub Token 提升到 5000 次/小时，或稍后再试";
    }

    private static long? ReadHeaderLong(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return null;
        }

        var text = values.FirstOrDefault();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonConfig.Options, ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 300 ? text : text[..300];
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? Truncate(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        return value.Length <= limit ? value : value[..limit] + "…";
    }

    private bool TryGetCached(string key, [NotNullWhen(true)] out AvailableUpdate? value)
    {
        value = null;
        if (!_cache.TryGetValue(key, out var item))
        {
            return false;
        }

        if (DateTimeOffset.Now - item.At < CacheDuration)
        {
            value = item.Value;
            return true;
        }

        _cache.TryRemove(key, out _);
        return false;
    }

    /// <summary>返回副本，避免调用方（界面改选下载文件）污染缓存。</summary>
    private static AvailableUpdate CloneUpdate(AvailableUpdate source) => new()
    {
        Version = source.Version,
        DownloadUrl = source.DownloadUrl,
        AssetName = source.AssetName,
        AssetSize = source.AssetSize,
        ReleasePageUrl = source.ReleasePageUrl,
        ReleaseNotes = source.ReleaseNotes,
        PublishedAt = source.PublishedAt,
        Advisory = source.Advisory,
        Candidates = source.Candidates
            .Select(c => new AssetCandidate { Name = c.Name, Url = c.Url, Size = c.Size, ContentType = c.ContentType })
            .ToList(),
    };

    private sealed record CacheItem(DateTimeOffset At, AvailableUpdate Value);

    /// <summary>GitHub Release JSON（只声明用到的字段）。</summary>
    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("assets")]
        public List<AssetDto>? Assets { get; set; }
    }

    /// <summary>GitHub Release Asset JSON。</summary>
    private sealed class AssetDto
    {
        [JsonPropertyName("url")]
        public string? ApiUrl { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }

        [JsonPropertyName("content_type")]
        public string? ContentType { get; set; }
    }
}
