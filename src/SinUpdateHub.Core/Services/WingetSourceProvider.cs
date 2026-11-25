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
/// WinGet 来源实现：不依赖本机 winget CLI，直接读 microsoft/winget-pkgs 官方清单仓库。
/// 流程：contents API 列出包 ID 的版本目录 → 取最大版本 → 拉 raw 清单 YAML →
/// 简易逐行解析 Installers 段，按 x64 &gt; arm64 &gt; neutral 选安装器直链。
/// 结果缓存 5 分钟；网络请求统一走 <see cref="NetworkClientFactory.Client"/>。
/// </summary>
public sealed class WingetSourceProvider : IUpdateSourceProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private const string ContentsPrefix = "https://api.github.com/repos/microsoft/winget-pkgs/contents/";
    private const string RawPrefix = "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/";
    private const string TreePrefix = "https://github.com/microsoft/winget-pkgs/tree/master/";

    /// <summary>清单里没有可直链安装包时的提示（msstore / Portable 等类型）。</summary>
    private const string NoInstallerAdvisory = "该包在清单里没有可直链安装包（可能是 msstore/Portable 类型），请到官网下载";

    private readonly NetworkClientFactory _factory;
    private readonly Func<AppSettings> _settings;
    private readonly WingetCatalogService? _catalog;
    private readonly ConcurrentDictionary<string, CacheItem> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="factory">HTTP 客户端工厂（负责代理 / Token / User-Agent）。</param>
    /// <param name="settings">设置访问器（读取"包含预发布版本"开关，用作缓存键的一部分）。</param>
    /// <param name="catalog">
    /// 可选的本地 WinGet 索引：有它就用本地索引取最新版本（不消耗 GitHub API 额度），
    /// 没有则退回 GitHub contents API 列版本目录。
    /// </param>
    public WingetSourceProvider(
        NetworkClientFactory factory,
        Func<AppSettings> settings,
        WingetCatalogService? catalog = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _catalog = catalog;
    }

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.WinGet;

    /// <summary>查询 WinGet 包的最新版本与安装器直链。</summary>
    public async Task<AvailableUpdate> GetLatestAsync(AppEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var id = ValidatePackageId(entry.Package);
        var includePrerelease = _settings().IncludePrerelease;
        var cacheKey = id + "|" + includePrerelease;

        if (TryGetCached(cacheKey, out var cached))
        {
            return CloneUpdate(cached);
        }

        var client = _factory.Client;
        var path = BuildManifestPath(id);

        // 优先用本地 WinGet 索引取最新版本（官方源数据，且不消耗 GitHub API 额度）
        string latest;
        var indexedVersion = _catalog is not null
            ? await _catalog.GetLatestVersionAsync(id, ct).ConfigureAwait(false)
            : null;

        if (!string.IsNullOrWhiteSpace(indexedVersion))
        {
            latest = indexedVersion!;
        }
        else
        {
            var versions = await GetVersionDirectoriesAsync(client, id, path, ct).ConfigureAwait(false);
            latest = versions
                .OrderByDescending(v => v, Comparer<string>.Create(VersionComparer.Compare))
                .First();
        }

        var installer = await FindInstallerAsync(client, id, path, latest, ct).ConfigureAwait(false);
        var releasePageUrl = $"{TreePrefix}{path}/{latest}";
        AssetCandidate? candidate = null;
        if (!string.IsNullOrWhiteSpace(installer?.Url))
        {
            var assetName = await ResolveInstallerNameAsync(client, installer!, id, latest, ct).ConfigureAwait(false);
            candidate = new AssetCandidate
            {
                Name = assetName,
                Url = installer.Url!,
                Size = null,
                ContentType = null,
            };
        }

        // 顺手取一下官网地址（清单 locale 文件里的 PackageUrl）：下载链接过期时可以直接去官网手动下载
        var homepage = await TryGetHomepageAsync(client, path, latest, id, ct).ConfigureAwait(false);

        var update = new AvailableUpdate
        {
            Version = latest,
            DownloadUrl = candidate?.Url,
            AssetName = candidate?.Name,
            AssetSize = null,
            ReleasePageUrl = releasePageUrl,
            HomepageUrl = homepage,
            Candidates = candidate is null ? new List<AssetCandidate>() : new List<AssetCandidate> { candidate },
        };

        if (candidate is null)
        {
            update.Advisory = NoInstallerAdvisory;
        }

        _cache[cacheKey] = new CacheItem(DateTimeOffset.Now, update);
        AppLog.Write($"WinGet 检查成功：{id} → {latest}（安装器：{candidate?.Name ?? "无直链"}）");
        return CloneUpdate(update);
    }

    /// <summary>清空结果缓存（设置变更或用户手动刷新时调用）。</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// 从清单的 locale 文件里读 PackageUrl（软件官网）。
    /// 用途：WinGet 的签名下载链接过期（401/403）时，可以引导用户去官网手动下载。
    /// </summary>
    private static async Task<string?> TryGetHomepageAsync(
        HttpClient client,
        string path,
        string version,
        string id,
        CancellationToken ct)
    {
        foreach (var locale in new[] { "zh-CN", "en-US" })
        {
            var url = $"{RawPrefix}{path}/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(id + ".locale." + locale + ".yaml")}";
            try
            {
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var yaml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                foreach (var line in yaml.Replace("\r\n", "\n").Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("PackageUrl:", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = trimmed["PackageUrl:".Length..].Trim().Trim('"', '\'');
                        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            return value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"读取 WinGet 官网地址失败：{url}", ex);
            }
        }

        return null;
    }

    /// <summary>
    /// 取安装包的"文件名"：URL 末尾就是 exe/msi/zip 时直接用；
    /// 遇到 download.php / getfile.aspx 这类动态下载地址时，先问服务器 Content-Disposition 要真实文件名，
    /// 拿不到就按清单的 InstallerType 合成（如 Yerong.OfficeToolPlus-11.9.15.0.exe），保证下载下来能运行。
    /// </summary>
    private static async Task<string> ResolveInstallerNameAsync(
        HttpClient client,
        YamlInstaller installer,
        string id,
        string version,
        CancellationToken ct)
    {
        var url = installer.Url!;
        var fromUrl = AssetMatcher.FileNameFromUrl(url);
        if (AssetMatcher.HasInstallerExtension(fromUrl))
        {
            return fromUrl;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var serverName = AssetMatcher.FileNameFromContentDisposition(response.Content.Headers.ContentDisposition);
                if (AssetMatcher.HasInstallerExtension(serverName))
                {
                    return AssetMatcher.Sanitize(serverName!);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"HEAD 获取安装包真实文件名失败：{url}", ex);
        }

        // 有些服务器不响应 HEAD（或 HEAD 不带 Content-Disposition），用「Range: bytes=0-0」的 GET 再问一次，
        // 这样能拿到真实文件名（例如 OfficeToolPlus 实际返回的是 Office_Tool_v11.9.15.0_x64.zip）。
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                var serverName = AssetMatcher.FileNameFromContentDisposition(response.Content.Headers.ContentDisposition);
                if (AssetMatcher.HasInstallerExtension(serverName))
                {
                    return AssetMatcher.Sanitize(serverName!);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Range GET 获取安装包真实文件名失败：{url}", ex);
        }

        var extension = AssetMatcher.ExtensionFromInstallerType(installer.Type);
        return AssetMatcher.Sanitize($"{id}-{version}{extension}");
    }

    /// <summary>包 ID 校验：非空、无空格、至少包含一个点（Publisher.Package 形式）。</summary>
    private static string ValidatePackageId(string? package)
    {
        var id = (package ?? string.Empty).Trim();
        if (id.Length == 0)
        {
            throw new UpdateSourceException("WinGet 包 ID 不能为空，请填写如 NetEase.UURemote");
        }

        if (id.Any(char.IsWhiteSpace))
        {
            throw new UpdateSourceException($"WinGet 包 ID 不能包含空格：「{id}」，示例：NetEase.UURemote");
        }

        var segments = id.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            throw new UpdateSourceException($"WinGet 包 ID 格式不正确：「{id}」至少需要包含一个点，示例：NetEase.UURemote");
        }

        return id;
    }

    /// <summary>
    /// 清单目录：manifests/&lt;首字母小写&gt;/&lt;Publisher&gt;/&lt;Package&gt;，
    /// 例如 NetEase.UURemote → manifests/n/NetEase/UURemote。
    /// </summary>
    private static string BuildManifestPath(string id)
    {
        var segments = id.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var first = segments[0];
        return "manifests/" + char.ToLowerInvariant(first[0]) + "/" + string.Join("/", segments);
    }

    /// <summary>用 contents API 列出包目录下的版本目录名。</summary>
    private static async Task<IReadOnlyList<string>> GetVersionDirectoriesAsync(
        HttpClient client,
        string id,
        string path,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(ContentsPrefix + path, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new UpdateSourceException($"WinGet 清单里找不到包 ID「{id}」，请检查拼写与大小写（示例：NetEase.UURemote）");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == (HttpStatusCode)429)
        {
            throw new UpdateSourceException(DescribeForbidden(response));
        }

        if (!response.IsSuccessStatusCode)
        {
            AppLog.Write($"WinGet 清单 contents 返回 HTTP {(int)response.StatusCode}：{path}");
            throw new UpdateSourceException($"WinGet 清单请求失败（HTTP {(int)response.StatusCode}），请检查网络或代理设置");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var items = await JsonSerializer.DeserializeAsync<List<ContentItemDto>>(stream, JsonConfig.Options, ct).ConfigureAwait(false)
                    ?? new List<ContentItemDto>();

        var versions = items
            .Where(i => string.Equals(i.Type, "dir", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => i.Name!.Trim())
            .ToList();

        if (versions.Count == 0)
        {
            throw new UpdateSourceException($"WinGet 清单里「{id}」没有任何版本目录，可能清单格式变化，请稍后再试");
        }

        return versions;
    }

    /// <summary>
    /// 按顺序尝试 installer.yaml、单清单 yaml；任何一份能解析出 InstallerUrl 即用。
    /// 都拿不到 URL 时返回 null（由上层给"请到官网下载"提示）。
    /// </summary>
    private static async Task<YamlInstaller?> FindInstallerAsync(
        HttpClient client,
        string id,
        string path,
        string version,
        CancellationToken ct)
    {
        var rawBase = $"{RawPrefix}{path}/{Uri.EscapeDataString(version)}";
        foreach (var fileName in new[] { id + ".installer.yaml", id + ".yaml" })
        {
            var url = $"{rawBase}/{Uri.EscapeDataString(fileName)}";
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                AppLog.Write($"WinGet 清单 {fileName} 返回 HTTP {(int)response.StatusCode}");
                continue;
            }

            var yaml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var chosen = ChooseInstaller(ParseInstallers(yaml));
            if (chosen is not null)
            {
                return chosen;
            }
        }

        return null;
    }

    /// <summary>
    /// 简易 YAML 逐行解析 Installers 段（不引入 YAML 库）：
    /// 每个以 "- " 开头的条目记录 Architecture / InstallerUrl / InstallerType / InstallerLocale / InstallerSha256；
    /// 没有 Installers 段时退回"全文第一个 InstallerUrl"。
    /// </summary>
    private static List<YamlInstaller> ParseInstallers(string? yaml)
    {
        var result = new List<YamlInstaller>();
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return result;
        }

        var lines = yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var inInstallers = false;
        YamlInstaller? current = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            if (!inInstallers)
            {
                if (trimmed.StartsWith("Installers:", StringComparison.OrdinalIgnoreCase))
                {
                    inInstallers = true;
                }

                continue;
            }

            // 退出 Installers 段：遇到顶格且非列表项的新键（如 ManifestType）
            var isListed = trimmed.StartsWith('-');
            if (!char.IsWhiteSpace(line[0]) && !isListed)
            {
                break;
            }

            if (isListed)
            {
                current = new YamlInstaller();
                result.Add(current);
                var inline = trimmed.Length > 1 ? trimmed[1..].Trim() : string.Empty;
                if (inline.Length > 0)
                {
                    ApplyProperty(current, inline);
                }

                continue;
            }

            if (current is not null)
            {
                ApplyProperty(current, trimmed);
            }
        }

        // 兼容：没有 Installers 段、或所有条目都没有 URL 时，取全文第一个 InstallerUrl
        if (result.All(e => string.IsNullOrWhiteSpace(e.Url)))
        {
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("InstallerUrl:", StringComparison.OrdinalIgnoreCase))
                {
                    var fallback = new YamlInstaller();
                    ApplyProperty(fallback, line);
                    if (!string.IsNullOrWhiteSpace(fallback.Url))
                    {
                        result.Add(fallback);
                    }

                    break;
                }
            }
        }

        return result;
    }

    /// <summary>解析 YAML 条目里的一行 "Key: Value"。</summary>
    private static void ApplyProperty(YamlInstaller entry, string text)
    {
        var idx = text.IndexOf(':');
        if (idx <= 0)
        {
            return;
        }

        var key = text[..idx].Trim();
        var value = text[(idx + 1)..].Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        if (value.Length == 0)
        {
            return;
        }

        switch (key.ToLowerInvariant())
        {
            case "architecture":
                entry.Architecture = value;
                break;
            case "installerurl":
                entry.Url = value;
                break;
            case "installertype":
                entry.Type = value;
                break;
            case "installerlocale":
                entry.Locale = value;
                break;
            case "installersha256":
                entry.Sha256 = value;
                break;
        }
    }

    /// <summary>选择安装器：x64 &gt; arm64 &gt; neutral &gt; 第一条；同架构优先 InstallerLocale = en-US。</summary>
    private static YamlInstaller? ChooseInstaller(IReadOnlyList<YamlInstaller> entries)
    {
        var withUrl = entries.Where(e => !string.IsNullOrWhiteSpace(e.Url)).ToList();
        if (withUrl.Count == 0)
        {
            return null;
        }

        foreach (var architecture in new[] { "x64", "arm64", "neutral" })
        {
            var matched = withUrl
                .Where(e => string.Equals(e.Architecture?.Trim(), architecture, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matched.Count == 0)
            {
                continue;
            }

            return matched.FirstOrDefault(e => string.Equals(e.Locale?.Trim(), "en-US", StringComparison.OrdinalIgnoreCase))
                   ?? matched[0];
        }

        return withUrl[0];
    }

    private static string DescribeForbidden(HttpResponseMessage response)
    {
        var remaining = ReadHeaderLong(response, "X-RateLimit-Remaining");
        if (response.StatusCode == (HttpStatusCode)429 || remaining is 0)
        {
            return "GitHub API 限流，暂无法查询 WinGet 清单。可在设置里填 GitHub Token 提升到 5000 次/小时，或稍后再试";
        }

        return "GitHub 拒绝访问 WinGet 清单（HTTP 403），请检查网络或代理设置";
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

    private static AvailableUpdate CloneUpdate(AvailableUpdate source) => new()
    {
        Version = source.Version,
        DownloadUrl = source.DownloadUrl,
        AssetName = source.AssetName,
        AssetSize = source.AssetSize,
        ReleasePageUrl = source.ReleasePageUrl,
        HomepageUrl = source.HomepageUrl,
        ReleaseNotes = source.ReleaseNotes,
        PublishedAt = source.PublishedAt,
        Advisory = source.Advisory,
        Candidates = source.Candidates
            .Select(c => new AssetCandidate { Name = c.Name, Url = c.Url, Size = c.Size, ContentType = c.ContentType })
            .ToList(),
    };

    private sealed record CacheItem(DateTimeOffset At, AvailableUpdate Value);

    /// <summary>manifest 的 YAML 安装器条目。</summary>
    private sealed class YamlInstaller
    {
        public string? Architecture { get; set; }
        public string? Url { get; set; }
        public string? Type { get; set; }
        public string? Locale { get; set; }
        public string? Sha256 { get; set; }
    }

    /// <summary>GitHub contents API 的目录项 JSON。</summary>
    private sealed class ContentItemDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }
}
