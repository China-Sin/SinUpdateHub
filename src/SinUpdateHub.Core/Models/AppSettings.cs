namespace SinUpdateHub.Core.Models;

/// <summary>应用设置（落盘时敏感字段由 SettingsStore 加密）。</summary>
public sealed class AppSettings
{
    public const string DefaultNoteFontFamily = "\"Microsoft YaHei UI\", \"Segoe UI\", system-ui, sans-serif";
    public const int DefaultNoteFontSize = 15;
    public const double DefaultNoteLineHeight = 1.72;
    public const int DefaultNoteZoomPercent = 100;
    public const int DefaultAutoBackupIntervalHours = 24;

    public int SchemaVersion { get; set; } = 1;

    // ---------- 网络 ----------
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;

    /// <summary>自定义代理地址，如 http://127.0.0.1:7897 或 socks5://127.0.0.1:7890。</summary>
    public string ProxyUrl { get; set; } = string.Empty;

    /// <summary>可选：GitHub API Token（未登录时 60 次/小时，加了提到 5000 次/小时）。</summary>
    public string GitHubToken { get; set; } = string.Empty;


    /// <summary>检查更新时是否包含 GitHub 的预发布版本。</summary>
    public bool IncludePrerelease { get; set; }

    // ---------- 行为 ----------
    public bool AutoCheckOnStartup { get; set; }

    /// <summary>下载缓存目录，空 = %LOCALAPPDATA%\SinUpdateHub\downloads。</summary>
    public string DownloadDir { get; set; } = string.Empty;

    /// <summary>安装器启动方式：true = 以管理员身份（UAC 提权）启动。</summary>
    public bool RunInstallerAsAdmin { get; set; } = true;

    // ---------- WebDAV 备份 ----------
    public string WebDavUrl { get; set; } = "https://dav.jianguoyun.com/dav/";
    public string WebDavUser { get; set; } = string.Empty;

    /// <summary>WebDAV 密码（落盘加密）。</summary>
    public string WebDavPassword { get; set; } = string.Empty;

    /// <summary>WebDAV 上的子目录名。</summary>
    public string WebDavFolder { get; set; } = "SinUpdateHub";

    /// <summary>服务端最多保留几个时间戳备份，多余的自动删除（0 = 不清理）。</summary>
    public int KeepBackups { get; set; } = 10;

    /// <summary>是否开启 WebDAV 自动定时备份（默认开启，仅在已配置 WebDAV 账号密码时执行）。</summary>
    public bool AutoBackupEnabled { get; set; } = true;

    /// <summary>自动定时备份间隔（小时），默认 24 小时（范围 1~720）。</summary>
    public int AutoBackupIntervalHours { get; set; } = DefaultAutoBackupIntervalHours;

    public DateTimeOffset? LastBackupAt { get; set; }
    public DateTimeOffset? LastRestoreAt { get; set; }

    /// <summary>是否已填全 WebDAV 地址、账号与密码。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsWebDavConfigured =>
        !string.IsNullOrWhiteSpace(WebDavUrl)
        && !string.IsNullOrWhiteSpace(WebDavUser)
        && !string.IsNullOrWhiteSpace(WebDavPassword);

    /// <summary>判断在指定时刻是否已到达下一次自动备份时间。</summary>
    public bool IsAutoBackupDue(DateTimeOffset now)
    {
        if (!AutoBackupEnabled || !IsWebDavConfigured)
        {
            return false;
        }

        if (LastBackupAt is null)
        {
            return true;
        }

        var hours = Math.Clamp(AutoBackupIntervalHours, 1, 720);
        return (now - LastBackupAt.Value) >= TimeSpan.FromHours(hours);
    }

    // ---------- SinNote 编辑器排版 ----------
    public string NoteFontFamily { get; set; } = DefaultNoteFontFamily;
    public int NoteFontSize { get; set; } = DefaultNoteFontSize;
    public double NoteLineHeight { get; set; } = DefaultNoteLineHeight;
    public int NoteZoomPercent { get; set; } = DefaultNoteZoomPercent;

    // ---------- 界面状态 ----------
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 780;
    public bool WindowMaximized { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
