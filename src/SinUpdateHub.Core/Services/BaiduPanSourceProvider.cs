using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// 百度网盘来源提供器（归属「软件下载」页）：
/// 不比较自动版本号；检查时若已登录百度网盘则验证远端路径是否有效。
/// </summary>
public sealed class BaiduPanSourceProvider : IUpdateSourceProvider
{
    private readonly BaiduPanService _baiduPan;
    private readonly Func<AppSettings> _settings;

    public BaiduPanSourceProvider(BaiduPanService baiduPan, Func<AppSettings> settings)
    {
        _baiduPan = baiduPan ?? throw new ArgumentNullException(nameof(baiduPan));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public SourceKind Kind => SourceKind.BaiduPan;

    public async Task<AvailableUpdate> GetLatestAsync(AppEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var remotePath = BaiduPanService.NormalizeRemoteDir(entry.RepoUrl);
        if (string.IsNullOrWhiteSpace(entry.RepoUrl))
        {
            throw new UpdateSourceException("百度网盘路径为空，请点「编辑」填写或从「百度网盘」页面添加");
        }

        var settings = _settings();
        if (!string.IsNullOrWhiteSpace(settings.BaiduAccessToken))
        {
            var isFile = string.Equals(entry.Package, "file", StringComparison.OrdinalIgnoreCase);
            var probeDir = isFile ? BaiduPanService.GetParentDir(remotePath) : remotePath;
            var entries = await _baiduPan.ListFilesAsync(settings, probeDir, onSettingsUpdated: null, ct).ConfigureAwait(false);
            if (isFile && !entries.Any(e => string.Equals(BaiduPanService.NormalizeRemoteDir(e.Path), remotePath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new UpdateSourceException($"百度网盘中找不到文件：{remotePath}");
            }
        }

        return new AvailableUpdate
        {
            Version = string.Empty,
            HomepageUrl = remotePath,
            ReleasePageUrl = remotePath,
        };
    }
}
