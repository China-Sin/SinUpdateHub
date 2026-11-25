namespace SinUpdateHub.Core.Models;

/// <summary>
/// 一条软件条目（用户维护的清单项）。
/// 对应界面上的一行：项目名称 / 包 / 当前版本 / 新版本 / 安装目录 / 来源。
/// </summary>
public sealed class AppEntry
{
    /// <summary>稳定 ID，用于备份合并、界面定位。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>项目名称，如 FreeType、UU 远程。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>列表中展示的名称；留空时默认显示 <see cref="Name"/>。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>来源类型。</summary>
    public SourceKind Source { get; set; } = SourceKind.GitHub;

    /// <summary>
    /// 包：
    /// - GitHub 来源：Assets 文件名筛选条件（包含的字符或正则），留空则自动挑最合适的安装包；
    /// - WinGet 来源：WinGet 包 ID，如 NetEase.UURemote。
    /// </summary>
    public string Package { get; set; } = string.Empty;

    /// <summary>Package 是否按正则解释（false = 普通"包含"匹配，不区分大小写）。</summary>
    public bool PackageIsRegex { get; set; }

    /// <summary>GitHub 仓库地址，如 https://github.com/OUBIGFA/FreeType（仅 Source=GitHub 使用）。</summary>
    public string RepoUrl { get; set; } = string.Empty;

    /// <summary>本机当前安装的版本（自动识别后回填，也可手填）。</summary>
    public string CurrentVersion { get; set; } = string.Empty;

    /// <summary>安装目录，如 D:\files\FreeType（自动识别后回填，也可手填）。</summary>
    public string InstallDir { get; set; } = string.Empty;

    /// <summary>
    /// 手动绑定的主程序 exe（「选 EXE」用）。一旦绑定，当前版本**始终从这个 exe 的文件版本读取**，
    /// 不再依赖注册表匹配（适合绿色/便携软件）。
    /// </summary>
    public string ExePath { get; set; } = string.Empty;

    /// <summary>安装器附加参数（如 /S、/SILENT、/quiet），可为空。</summary>
    public string InstallArgs { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    public string Remark { get; set; } = string.Empty;

    // ---------- 以下是"检查更新"产生的快照，落盘便于重启后仍能看到上次结果 ----------

    /// <summary>新版本（检查结果）。</summary>
    public string? LatestVersion { get; set; }

    /// <summary>GitHub 来源：Actual 匹配到的 Assets 文件名；WinGet 来源：清单里的安装器文件名。</summary>
    public string? MatchedAssetName { get; set; }

    /// <summary>下载地址（检查时解析出来的直链）。</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>发布页 / 清单页地址。</summary>
    public string? ReleasePageUrl { get; set; }

    /// <summary>软件官网（WinGet 清单里 PackageUrl，用于下载链接失效时去官网手动下载）。</summary>
    public string? HomepageUrl { get; set; }

    /// <summary>上次检查时间。</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>上次检查失败原因。</summary>
    public string? LastError { get; set; }

    public AppEntry Clone() => new()
    {
        Id = Id,
        Name = Name,
        DisplayName = DisplayName,
        Source = Source,
        Package = Package,
        PackageIsRegex = PackageIsRegex,
        RepoUrl = RepoUrl,
        CurrentVersion = CurrentVersion,
        InstallDir = InstallDir,
        ExePath = ExePath,
        InstallArgs = InstallArgs,
        Remark = Remark,
        LatestVersion = LatestVersion,
        MatchedAssetName = MatchedAssetName,
        DownloadUrl = DownloadUrl,
        ReleasePageUrl = ReleasePageUrl,
        HomepageUrl = HomepageUrl,
        LastCheckedAt = LastCheckedAt,
        LastError = LastError,
    };

    /// <summary>把另一个条目的可编辑字段复制过来（保留 ID）。</summary>
    public void CopyEditableFrom(AppEntry other)
    {
        Name = other.Name;
        DisplayName = other.DisplayName;
        Source = other.Source;
        Package = other.Package;
        PackageIsRegex = other.PackageIsRegex;
        RepoUrl = other.RepoUrl;
        CurrentVersion = other.CurrentVersion;
        InstallDir = other.InstallDir;
        ExePath = other.ExePath;
        InstallArgs = other.InstallArgs;
        Remark = other.Remark;
        HomepageUrl = other.HomepageUrl;
    }

    public override string ToString() => $"{Name} [{Source}] {Package}";
}
