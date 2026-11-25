namespace SinUpdateHub.Core.Models;

/// <summary>一个可下载的候选文件（GitHub Assets / WinGet 安装器）。</summary>
public sealed class AssetCandidate
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public long? Size { get; set; }
    public string? ContentType { get; set; }

    public override string ToString() => Name;
}

/// <summary>检查更新得到的"最新可用版本"。</summary>
public sealed class AvailableUpdate
{
    /// <summary>最新版本号（已去掉前缀 v）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>最终选定的下载直链（可空：比如只解析出版本没解析到包）。</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>最终选定的文件名。</summary>
    public string? AssetName { get; set; }

    public long? AssetSize { get; set; }

    /// <summary>发布页 / 清单页。</summary>
    public string? ReleasePageUrl { get; set; }

    /// <summary>软件官网（WinGet 清单的 PackageUrl，用于下载链接失效时去官网手动下载）。</summary>
    public string? HomepageUrl { get; set; }

    /// <summary>发布说明（GitHub body 摘要）。</summary>
    public string? ReleaseNotes { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>所有匹配到或可选的候选文件（用于让用户改选）。</summary>
    public List<AssetCandidate> Candidates { get; set; } = new();

    /// <summary>附加提示（例如"当前版本无法比较，请手动确认"）。</summary>
    public string? Advisory { get; set; }
}

/// <summary>单条目检查结果。</summary>
public sealed class CheckResult
{
    public string EntryId { get; set; } = string.Empty;
    public UpdateState State { get; set; } = UpdateState.Unknown;
    public AvailableUpdate? Update { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;

    public static CheckResult Fail(string entryId, string error) => new()
    {
        EntryId = entryId,
        State = UpdateState.Failed,
        Error = error,
        CheckedAt = DateTimeOffset.Now,
    };
}

/// <summary>本机扫描到的已安装软件（注册表卸载项 / 安装目录 exe 文件版本）。</summary>
public sealed class DetectedApp
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? InstallLocation { get; set; }
    public string? Publisher { get; set; }
    public string? UninstallString { get; set; }
    public string? DisplayIcon { get; set; }

    /// <summary>来源说明，如 "HKLM64\...\Uninstall\XXX" 或 "FileVersion: D:\files\FreeType\FreeType.exe"。</summary>
    public string? DetectedFrom { get; set; }

    /// <summary>匹配得分（仅在按条目查找候选时使用）。</summary>
    public double Score { get; set; }

    public string Summary
    {
        get
        {
            var v = string.IsNullOrWhiteSpace(Version) ? "版本未知" : Version;
            var d = string.IsNullOrWhiteSpace(InstallLocation) ? "目录未知" : InstallLocation;
            return $"{DisplayName}  ·  {v}  ·  {d}";
        }
    }

    public override string ToString() => Summary;
}
