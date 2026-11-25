namespace SinUpdateHub.Core.Services;

/// <summary>应用的本地路径约定（便携与安装位置统一走这里）。</summary>
public static class AppPaths
{
    public const string ProductName = "SinUpdateHub";

    /// <summary>配置根目录：%APPDATA%\SinUpdateHub</summary>
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductName);

    public static string DataFile => Path.Combine(Root, "data.json");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LogFile => Path.Combine(Root, "logs", "app.log");

    public static string DefaultDownloadDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName, "downloads");

    public static string DownloadDir(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultDownloadDir : Environment.ExpandEnvironmentVariables(configured.Trim());

    /// <summary>确保目录存在。</summary>
    public static void Ensure(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch
        {
            // 目录创建失败时由调用方处理（例如回退到默认目录）
        }
    }

    public static void EnsureAll(string? downloadDir = null)
    {
        Ensure(Root);
        Ensure(Path.GetDirectoryName(LogFile)!);
        Ensure(DownloadDir(downloadDir));
    }
}

/// <summary>极简文件日志（不抛异常，失败就静默）。</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(AppPaths.LogFile);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            if (ex is not null)
            {
                line += Environment.NewLine + ex;
            }

            lock (Gate)
            {
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // ignore
        }
    }
}
