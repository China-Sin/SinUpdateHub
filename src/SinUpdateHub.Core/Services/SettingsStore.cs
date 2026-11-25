using System.Text;
using System.Text.Json;
using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// 设置持久化（默认 %APPDATA%\SinUpdateHub\settings.json）：
/// - 落盘前用 <see cref="SecretProtector"/> 加密 WebDavPassword / GitHubToken（文件中为 dpapi:...）；
/// - 读取时解密；缺字段用 <see cref="AppSettings"/> 默认值补齐；损坏时回退默认设置并写日志；
/// - 保存用 .tmp + 原子替换，避免写一半断电导致设置全丢；
/// - 内部加锁，可被 UI 线程与后台备份线程同时调用。
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly object _gate = new();

    /// <inheritdoc />
    public string FilePath { get; }

    /// <param name="filePath">设置文件路径；null/空 = <see cref="AppPaths.SettingsFile"/>（测试可传临时文件）。</param>
    public SettingsStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? AppPaths.SettingsFile : filePath.Trim();
    }

    /// <summary>读取设置：文件不存在 / 解析失败 / 字段缺失都安全回退到默认值。</summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    /// <summary>保存设置：敏感字段加密后 tmp + 原子替换落盘，不影响调用方手里的明文对象。</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            SaveCore(settings);
        }
    }

    private AppSettings LoadCore()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            var stored = JsonSerializer.Deserialize<AppSettings>(json, JsonConfig.Options);
            if (stored is null)
            {
                return new AppSettings();
            }

            stored.WebDavPassword = SecretProtector.Unprotect(stored.WebDavPassword);
            stored.GitHubToken = SecretProtector.Unprotect(stored.GitHubToken);

            // 手改 JSON 把字段写成 null 时的兜底（不允许非空字段变 null）
            var defaults = new AppSettings();
            stored.ProxyUrl ??= defaults.ProxyUrl;
            stored.GitHubToken ??= defaults.GitHubToken;
            stored.DownloadDir ??= defaults.DownloadDir;
            stored.WebDavUrl ??= defaults.WebDavUrl;
            stored.WebDavUser ??= defaults.WebDavUser;
            stored.WebDavPassword ??= defaults.WebDavPassword;
            if (string.IsNullOrWhiteSpace(stored.WebDavFolder))
            {
                stored.WebDavFolder = defaults.WebDavFolder;
            }

            if (string.IsNullOrWhiteSpace(stored.NoteFontFamily))
            {
                stored.NoteFontFamily = defaults.NoteFontFamily;
            }

            if (stored.NoteFontSize is < 10 or > 32)
            {
                stored.NoteFontSize = defaults.NoteFontSize;
            }

            if (stored.NoteLineHeight is < 1.1 or > 3.0 || double.IsNaN(stored.NoteLineHeight))
            {
                stored.NoteLineHeight = defaults.NoteLineHeight;
            }

            if (stored.NoteZoomPercent is < 50 or > 200)
            {
                stored.NoteZoomPercent = defaults.NoteZoomPercent;
            }

            if (stored.AutoBackupIntervalHours is < 1 or > 720)
            {
                stored.AutoBackupIntervalHours = defaults.AutoBackupIntervalHours;
            }

            return stored;
        }
        catch (Exception ex)
        {
            AppLog.Write($"读取设置失败，已回退默认设置：{FilePath}", ex);
            return new AppSettings();
        }
    }

    private void SaveCore(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stored = settings.Clone();
        stored.WebDavPassword = SecretProtector.Protect(settings.WebDavPassword);
        stored.GitHubToken = SecretProtector.Protect(settings.GitHubToken);

        var json = JsonSerializer.Serialize(stored, JsonConfig.Options);
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json, Utf8NoBom);
        File.Move(tempPath, FilePath, overwrite: true);
        AppLog.Write($"设置已保存：{FilePath}");
    }
}
