using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Abstractions;

/// <summary>更新来源（GitHub Releases / WinGet 清单）。</summary>
public interface IUpdateSourceProvider
{
    SourceKind Kind { get; }

    /// <summary>
    /// 查询某个条目的最新版本。
    /// 找不到版本、网络失败、包 ID 无效等情况请抛 <see cref="UpdateSourceException"/> 或普通异常，
    /// 由上层统一转成失败状态并展示。
    /// </summary>
    Task<AvailableUpdate> GetLatestAsync(AppEntry entry, CancellationToken ct = default);
}

/// <summary>来源查询的可预期错误（用于给用户友好的中文提示）。</summary>
public sealed class UpdateSourceException : Exception
{
    public UpdateSourceException(string message) : base(message) { }
    public UpdateSourceException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 下载地址被服务器拒绝（401/403/404/410）：通常是清单里的签名下载链接过期，
/// 需要重新解析一次最新地址再重试。
/// </summary>
public sealed class DownloadRejectedException : Exception
{
    public DownloadRejectedException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

/// <summary>本机已安装软件识别（注册表卸载项 / 安装目录 exe 文件版本）。</summary>
public interface IInstalledAppDetector
{
    /// <summary>扫描本机已安装软件（结果缓存，重启或 Refresh 后更新）。</summary>
    Task<IReadOnlyList<DetectedApp>> ScanInstalledAsync(bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>根据条目的名称/包 ID/安装目录，给出可能对应的已装软件候选（按相关度排序）。</summary>
    Task<IReadOnlyList<DetectedApp>> FindCandidatesAsync(AppEntry entry, CancellationToken ct = default);

    /// <summary>从指定安装目录里读主程序 exe 的文件版本，作为"当前版本"兜底。</summary>
    Task<DetectedApp?> ResolveFromInstallDirAsync(string installDir, CancellationToken ct = default);

    /// <summary>仅从指定安装目录里读主程序 exe 的文件版本（不回退注册表）。</summary>
    Task<DetectedApp?> ResolveExeOnlyFromInstallDirAsync(string installDir, CancellationToken ct = default);

    /// <summary>读取指定 exe 的文件版本，并把它所在目录作为安装目录（用户手动选 exe 时用）。</summary>
    Task<DetectedApp?> ResolveFromExecutableAsync(string exePath, CancellationToken ct = default);
}

/// <summary>下载安装包。</summary>
public interface IDownloadService
{
    /// <summary>下载到缓存目录，返回本地完整路径。</summary>
    Task<string> DownloadAsync(
        string url,
        string targetFileName,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>启动安装器。</summary>
public interface IInstallerRunner
{
    Task<InstallerResult> RunAsync(string filePath, string? arguments, bool runAsAdmin, CancellationToken ct = default);
}

/// <summary>WebDAV 备份。</summary>
public interface IWebDavBackupService
{
    Task<WebDavCheckResult> CheckAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>列出远端目录下的备份文件（新的在前）。</summary>
    Task<IReadOnlyList<BackupFileInfo>> ListAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>上传清单：总是写一份 latest 快照，并按需写一份时间戳备份。</summary>
    Task<BackupUploadResult> UploadAsync(AppSettings settings, AppData data, bool makeTimestamped = true, CancellationToken ct = default);

    Task<AppData> DownloadAsync(AppSettings settings, string fileName, CancellationToken ct = default);

    /// <summary>删除远端某个备份文件。</summary>
    Task DeleteAsync(AppSettings settings, string fileName, CancellationToken ct = default);
}

/// <summary>清单持久化。</summary>
public interface IAppStore
{
    string FilePath { get; }
    AppData Load();
    void Save(AppData data);
    void Export(AppData data, string path);
    AppData Import(string path);
}

/// <summary>设置持久化（敏感字段加密落盘）。</summary>
public interface ISettingsStore
{
    string FilePath { get; }
    AppSettings Load();
    void Save(AppSettings settings);
}
