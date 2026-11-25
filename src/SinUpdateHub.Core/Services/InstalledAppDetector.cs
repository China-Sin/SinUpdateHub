using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// 已安装软件识别：
/// 1. 扫描注册表卸载项（HKLM 64 位 / HKLM 32 位 / HKCU），得到名称、版本、安装目录；
/// 2. 卸载项没有 DisplayVersion 时，退回到 DisplayIcon / InstallLocation 下 exe 的文件版本；
/// 3. 按名称 / 包 ID / 仓库名 / 安装目录做相关度打分，给出"自动识别"候选列表；
/// 4. 支持直接从指定安装目录读主程序 exe 版本。
/// 扫描结果缓存 90 秒，避免每次刷新界面都遍历注册表。
/// </summary>
public sealed class InstalledAppDetector : IInstalledAppDetector
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(90);

    private static readonly Regex KbNameRegex = new(@"^KB\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] UpdateReleaseTypes = { "update", "hotfix", "security update" };

    private static readonly string[] AppxFrameworkPrefixes =
    {
        "Microsoft.VCLibs.",
        "Microsoft.UI.Xaml.",
        "Microsoft.NET.Native.",
        "Microsoft.WindowsAppRuntime.",
        "Microsoft.WinAppRuntime.",
        "Microsoft.Services.Store.",
        "Microsoft.Advertising.",
        "Microsoft.DirectX.",
        "Microsoft.Windows.AppRuntime.",
    };

    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private IReadOnlyList<DetectedApp>? _cache;
    private DateTimeOffset _cacheAt;

    /// <summary>扫描本机已安装软件（默认使用 90 秒缓存；<paramref name="forceRefresh"/> 强制重扫）。</summary>
    public async Task<IReadOnlyList<DetectedApp>> ScanInstalledAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && TryGetCache(out var cached))
        {
            return cached;
        }

        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && TryGetCache(out cached))
            {
                return cached;
            }

            var list = await Task.Run(() => ScanRegistry(), ct).ConfigureAwait(false);
            _cache = list;
            _cacheAt = DateTimeOffset.Now;
            AppLog.Write($"已安装软件扫描完成：{list.Count} 条（缓存 90 秒）");
            return list;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>
    /// 按名称 / 包 ID / 仓库名 / 安装目录给已安装软件打分，返回得分大于 0 的前 8 个候选（分数从高到低）。
    /// 用于界面「自动识别」按钮：用户点一下就从候选里挑出对应的软件。
    /// </summary>
    public async Task<IReadOnlyList<DetectedApp>> FindCandidatesAsync(AppEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var installed = await ScanInstalledAsync(false, ct).ConfigureAwait(false);
        var scored = new List<DetectedApp>();
        foreach (var app in installed)
        {
            ct.ThrowIfCancellationRequested();
            var score = ScoreCandidate(entry, app);
            if (score <= 0)
            {
                continue;
            }

            var copy = CloneDetected(app);
            copy.Score = score;
            scored.Add(copy);
        }

        return scored
            .OrderByDescending(a => a.Score)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .ToList();
    }

    /// <summary>
    /// 从指定安装目录读取主程序版本：优先读取目录里主程序 exe 的文件版本（排除卸载程序），
    /// 读不到时再按目录反查注册表卸载项。
    /// 目录不存在时返回 null；目录存在但读不到版本时返回 Version 为 null、InstallLocation 有值的对象
    /// （这样界面仍能把安装目录回填给用户，版本留空手填）。
    /// </summary>
    public async Task<DetectedApp?> ResolveFromInstallDirAsync(string installDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return null;
        }

        var fromExe = await Task.Run(() => ResolveFromInstallDirCore(installDir), ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fromExe?.Version))
        {
            return fromExe;
        }

        var expanded = Environment.ExpandEnvironmentVariables(installDir.Trim().Trim('"'));
        var normalizedTarget = NormalizePath(expanded);
        if (normalizedTarget.Length > 0)
        {
            var installed = await ScanInstalledAsync(false, ct).ConfigureAwait(false);
            var matched = installed
                .Where(a => !string.IsNullOrWhiteSpace(a.Version)
                            && NormalizePath(a.InstallLocation) == normalizedTarget)
                .OrderByDescending(a => a.Version, Comparer<string?>.Create(VersionComparer.Compare))
                .FirstOrDefault();
            if (matched is not null)
            {
                return CloneDetected(matched);
            }
        }

        return fromExe;
    }

    /// <summary>仅读取安装目录内主程序 EXE 的文件版本（不回退注册表），用于判断 EXE 自身是否带版本号。</summary>
    public Task<DetectedApp?> ResolveExeOnlyFromInstallDirAsync(string installDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return Task.FromResult<DetectedApp?>(null);
        }

        return Task.Run(() => ResolveFromInstallDirCore(installDir), ct);
    }

    /// <summary>从识别到的软件信息里尽量解析出主程序 exe 路径（用于自动绑定）。</summary>
    public static string? ResolveMainExePath(DetectedApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var iconPath = ParseIconPath(app.DisplayIcon);
        if (!string.IsNullOrWhiteSpace(iconPath)
            && iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !IsWindowsAppsAliasPath(iconPath)
            && !IsUninstallerExe(iconPath)
            && File.Exists(iconPath))
        {
            return iconPath;
        }

        if (string.IsNullOrWhiteSpace(app.InstallLocation) || !Directory.Exists(app.InstallLocation))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(app.InstallLocation);
            var dirName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var normalizedDirName = NormalizeName(dirName);
            var normalizedAppName = NormalizeName(app.DisplayName);

            var candidates = Directory
                .EnumerateFiles(full, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(exe => !IsUninstallerExe(exe) && !IsWindowsAppsAliasPath(exe))
                .Take(60)
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            string? best = null;
            var bestScore = -1;
            foreach (var exe in candidates)
            {
                var exeName = NormalizeName(Path.GetFileNameWithoutExtension(exe));
                if (exeName.Length == 0)
                {
                    continue;
                }

                var score = 0;
                if (normalizedDirName.Length > 0)
                {
                    if (exeName == normalizedDirName) score = Math.Max(score, 4);
                    else if (exeName.Contains(normalizedDirName, StringComparison.Ordinal) || normalizedDirName.Contains(exeName, StringComparison.Ordinal)) score = Math.Max(score, 2);
                }

                if (normalizedAppName.Length > 0)
                {
                    if (exeName == normalizedAppName) score = Math.Max(score, 3);
                    else if (exeName.Contains(normalizedAppName, StringComparison.Ordinal) || normalizedAppName.Contains(exeName, StringComparison.Ordinal)) score = Math.Max(score, 1);
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = exe;
                }
            }

            return bestScore > 0 ? best : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>判断路径是否为 WindowsApps 执行别名（MSIX/商店应用占位 exe）。</summary>
    public static bool IsWindowsAppsAliasPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return path.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase)
               || path.Contains(@"/Microsoft/WindowsApps/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 读取用户手动选中的 exe：用它的文件版本作为当前版本，所在目录作为安装目录。
    /// 若选中 WindowsApps 执行别名，则自动从本机 MSIX/Appx 注册信息匹配对应包版本。
    /// </summary>
    public Task<DetectedApp?> ResolveFromExecutableAsync(string exePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return Task.FromResult<DetectedApp?>(null);
        }

        if (IsWindowsAppsAliasPath(exePath))
        {
            return ResolveFromWindowsAppsAliasAsync(exePath, ct);
        }

        return Task.Run(() => ResolveFromExecutableCore(exePath), ct);
    }

    private async Task<DetectedApp?> ResolveFromWindowsAppsAliasAsync(string exePath, CancellationToken ct)
    {
        var aliasName = NormalizeName(Path.GetFileNameWithoutExtension(exePath));
        if (aliasName.Length == 0)
        {
            return null;
        }

        var installed = await ScanInstalledAsync(false, ct).ConfigureAwait(false);
        DetectedApp? best = null;
        var bestScore = 0d;

        foreach (var app in installed)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(app.DetectedFrom)
                || !app.DetectedFrom.StartsWith("MSIX/Appx:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fullName = app.DetectedFrom["MSIX/Appx:".Length..].Trim();
            var parsed = ParseAppxPackageFullName(fullName);
            var pkgName = parsed?.PackageName ?? string.Empty;
            var pkgShort = pkgName.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

            var normDisplay = NormalizeName(app.DisplayName);
            var normPkg = NormalizeName(pkgName);
            var normShort = NormalizeName(pkgShort);

            var score = 0d;
            if (aliasName == normDisplay || aliasName == normShort || aliasName == normPkg)
            {
                score += 100;
            }
            else if ((normDisplay.Length >= 2 && (normDisplay.Contains(aliasName, StringComparison.Ordinal) || aliasName.Contains(normDisplay, StringComparison.Ordinal)))
                     || (normShort.Length >= 2 && (normShort.Contains(aliasName, StringComparison.Ordinal) || aliasName.Contains(normShort, StringComparison.Ordinal)))
                     || (normPkg.Length >= 2 && normPkg.Contains(aliasName, StringComparison.Ordinal)))
            {
                score += 50;
            }

            if (score <= 0)
            {
                continue;
            }

            score += Similarity(aliasName, normDisplay) * 30;
            if (!string.IsNullOrWhiteSpace(app.Version))
            {
                score += 10;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = app;
            }
        }

        return best is null ? null : CloneDetected(best);
    }

    private static DetectedApp? ResolveFromExecutableCore(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return null;
        }

        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            var version = !string.IsNullOrWhiteSpace(info.FileVersion)
                ? info.FileVersion
                : info.ProductVersion;
            version = VersionComparer.Normalize(version);

            var name = !string.IsNullOrWhiteSpace(info.ProductName)
                ? info.ProductName!
                : !string.IsNullOrWhiteSpace(info.FileDescription)
                    ? info.FileDescription!
                    : Path.GetFileNameWithoutExtension(exePath);

            return new DetectedApp
            {
                DisplayName = name,
                Version = string.IsNullOrWhiteSpace(version) ? null : version,
                InstallLocation = Path.GetDirectoryName(exePath),
                DetectedFrom = "FileVersion: " + exePath,
            };
        }
        catch (Exception ex)
        {
            AppLog.Write($"读取 exe 文件版本失败：{exePath}", ex);
            return null;
        }
    }

    private bool TryGetCache(out IReadOnlyList<DetectedApp> cached)
    {
        var cache = _cache;
        if (cache is not null && DateTimeOffset.Now - _cacheAt < CacheDuration)
        {
            cached = cache;
            return true;
        }

        cached = Array.Empty<DetectedApp>();
        return false;
    }

    // ---------------- 注册表扫描 ----------------

    private static List<DetectedApp> ScanRegistry()
    {
        var result = new List<DetectedApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ScanHive(result, seen, RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64");
        ScanHive(result, seen, RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32");
        ScanHive(result, seen, RegistryHive.CurrentUser, RegistryView.Default, "HKCU");
        ScanAppxPackages(result, seen);

        return result;
    }

    private static void ScanAppxPackages(List<DetectedApp> result, HashSet<string> seen)
    {
        try
        {
            using var packagesKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packagesKey is null)
            {
                return;
            }

            var bestByPackage = new Dictionary<string, DetectedApp>(StringComparer.OrdinalIgnoreCase);
            foreach (var fullName in packagesKey.GetSubKeyNames())
            {
                try
                {
                    var parsed = ParseAppxPackageFullName(fullName);
                    if (parsed is null)
                    {
                        continue;
                    }

                    var (packageName, version) = parsed.Value;
                    if (AppxFrameworkPrefixes.Any(prefix => packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    using var key = packagesKey.OpenSubKey(fullName);
                    if (key is null)
                    {
                        continue;
                    }

                    var rootFolder = ReadString(key, "PackageRootFolder");
                    if (!string.IsNullOrWhiteSpace(rootFolder)
                        && (rootFolder.Contains(@"\Windows\SystemApps", StringComparison.OrdinalIgnoreCase)
                            || rootFolder.Contains(@"\Windows\ImmersiveControlPanel", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var rawDisplayName = ReadString(key, "DisplayName");
                    var displayName = ResolveAppxDisplayName(rawDisplayName, fullName, packageName);
                    var installLocation = !string.IsNullOrWhiteSpace(rootFolder)
                                          && !rootFolder.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
                        ? NormalizeDirectory(rootFolder)
                        : null;

                    var candidate = new DetectedApp
                    {
                        DisplayName = displayName,
                        Version = version,
                        InstallLocation = installLocation,
                        DetectedFrom = $"MSIX/Appx: {fullName}",
                    };

                    if (!bestByPackage.TryGetValue(packageName, out var existing)
                        || VersionComparer.Compare(existing.Version, candidate.Version) < 0)
                    {
                        bestByPackage[packageName] = candidate;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write($"读取 MSIX/Appx 注册表项失败：{fullName}", ex);
                }
            }

            foreach (var app in bestByPackage.Values)
            {
                var dedupeKey = string.Join(
                    '|',
                    app.DisplayName,
                    app.Version ?? string.Empty,
                    NormalizePath(app.InstallLocation));
                if (seen.Add(dedupeKey))
                {
                    result.Add(app);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("扫描 MSIX/Appx 已安装应用失败", ex);
        }
    }

    internal static (string PackageName, string Version)? ParseAppxPackageFullName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return null;
        }

        var parts = fullName.Trim().Split('_');
        if (parts.Length < 2)
        {
            return null;
        }

        var packageName = parts[0].Trim();
        var version = parts[1].Trim();
        if (packageName.Length == 0
            || version.Length == 0
            || !version.Contains('.')
            || version.StartsWith('.')
            || version.EndsWith('.')
            || !version.All(c => char.IsDigit(c) || c == '.'))
        {
            return null;
        }

        return (packageName, version);
    }

    private static string ResolveAppxDisplayName(string? rawDisplayName, string packageFullName, string packageName)
    {
        var name = rawDisplayName?.Trim();
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (name.StartsWith("@{", StringComparison.Ordinal))
            {
                name = TryLoadIndirectString(name);
            }
            else if (name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                var resourceKey = name["ms-resource:".Length..].TrimStart('/');
                name = TryLoadIndirectString($"@{{{packageFullName}?ms-resource://{packageName}/Resources/{resourceKey}}}");
            }
        }

        if (!string.IsNullOrWhiteSpace(name)
            && !name.StartsWith("@{", StringComparison.Ordinal)
            && !name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        var lastSegment = packageName.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return !string.IsNullOrWhiteSpace(lastSegment) && lastSegment.Any(char.IsLetter)
            ? lastSegment
            : packageName;
    }

    private static string? TryLoadIndirectString(string source)
    {
        try
        {
            var buffer = new StringBuilder(512);
            var hr = SHLoadIndirectString(source, buffer, buffer.Capacity, IntPtr.Zero);
            if (hr == 0)
            {
                var resolved = buffer.ToString().Trim();
                return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(
        string pszSource,
        StringBuilder pszOutBuf,
        int cchOutBuf,
        IntPtr ppvReserved);

    private static void ScanHive(
        List<DetectedApp> result,
        HashSet<string> seen,
        RegistryHive hive,
        RegistryView view,
        string label)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null)
            {
                return;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(subKeyName);
                    if (key is null)
                    {
                        continue;
                    }

                    var app = ReadApp(key, label, subKeyName);
                    if (app is null)
                    {
                        continue;
                    }

                    var dedupeKey = string.Join(
                        '|',
                        app.DisplayName,
                        app.Version ?? string.Empty,
                        NormalizePath(app.InstallLocation));
                    if (seen.Add(dedupeKey))
                    {
                        result.Add(app);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write($"读取注册表卸载项失败：{label}\\{subKeyName}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"扫描注册表卸载项失败：{label}", ex);
        }
    }

    /// <summary>读取单个卸载项；返回 null 表示应跳过（系统组件 / 补丁 / 无名称）。</summary>
    private static DetectedApp? ReadApp(RegistryKey key, string label, string subKeyName)
    {
        var displayName = ReadString(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        // 补丁 / 系统组件不算"已安装软件"
        if (KbNameRegex.IsMatch(displayName.Trim()))
        {
            return null;
        }

        if (IsSystemComponent(key))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(ReadString(key, "ParentKeyName")))
        {
            return null;
        }

        var releaseType = ReadString(key, "ReleaseType");
        if (releaseType is not null &&
            UpdateReleaseTypes.Any(t => releaseType.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var version = ReadString(key, "DisplayVersion");
        var displayIcon = ReadString(key, "DisplayIcon");
        var installLocation = NormalizeDirectory(ReadString(key, "InstallLocation"));

        var app = new DetectedApp
        {
            DisplayName = displayName.Trim(),
            Version = CleanVersion(version),
            InstallLocation = installLocation,
            Publisher = ReadString(key, "Publisher"),
            UninstallString = ReadString(key, "UninstallString"),
            DisplayIcon = displayIcon,
            DetectedFrom = $@"{label}\...\Uninstall\{subKeyName}",
        };

        if (string.IsNullOrWhiteSpace(app.Version))
        {
            // DisplayVersion 缺失：从 DisplayIcon / 安装目录的 exe 文件版本兜底
            app.Version = VersionFromDisplayIcon(displayIcon) ?? VersionFromInstallDir(installLocation);
        }

        return app;
    }

    private static bool IsSystemComponent(RegistryKey key)
    {
        try
        {
            var value = key.GetValue("SystemComponent");
            if (value is null)
            {
                return false;
            }

            return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadString(RegistryKey key, string name)
    {
        try
        {
            var value = key.GetValue(name);
            var text = value switch
            {
                null => null,
                string s => s,
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            };

            text = text?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static string? VersionFromDisplayIcon(string? displayIcon)
    {
        var path = ParseIconPath(displayIcon);
        if (path is null || !File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return VersionFromExe(path);
    }

    /// <summary>解析 DisplayIcon：支持 "C:\a\b.exe"、"C:\a\b.exe,0"、带空格的可执行文件路径。</summary>
    private static string? ParseIconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var text = displayIcon.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close > 1)
            {
                return Environment.ExpandEnvironmentVariables(text[1..close]);
            }
        }

        var comma = text.LastIndexOf(',');
        if (comma > 0 && text[(comma + 1)..].Trim().All(char.IsDigit))
        {
            text = text[..comma];
        }

        text = text.Trim().Trim('"');
        var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
        {
            text = text[..(exeIndex + 4)];
        }

        return string.IsNullOrWhiteSpace(text) ? null : Environment.ExpandEnvironmentVariables(text);
    }

    internal static bool IsUninstallerExe(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("setup", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("remover", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith("uninstall", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith("uninstaller", StringComparison.OrdinalIgnoreCase);
    }

    private static string? VersionFromInstallDir(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
        {
            return null;
        }

        try
        {
            var count = 0;
            foreach (var exe in Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (IsUninstallerExe(exe))
                {
                    continue;
                }

                if (count++ >= 20)
                {
                    break;
                }

                var version = VersionFromExe(exe);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"读取安装目录 exe 版本失败：{installLocation}", ex);
        }

        return null;
    }

    // ---------------- 安装目录解析 ----------------

    private static DetectedApp? ResolveFromInstallDirCore(string installDir)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(installDir.Trim().Trim('"'));
            if (!Directory.Exists(expanded))
            {
                return null;
            }

            var full = Path.GetFullPath(expanded);
            var dirName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var normalizedDirName = NormalizeName(dirName);

            var exeFiles = Directory
                .EnumerateFiles(full, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(exe => !IsUninstallerExe(exe))
                .Take(60)
                .ToList();

            var found = new List<(string Exe, string Version, int Priority)>();
            foreach (var exe in exeFiles)
            {
                var version = VersionFromExe(exe);
                if (string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                var exeName = NormalizeName(Path.GetFileNameWithoutExtension(exe));
                var priority = exeName.Length == 0 || normalizedDirName.Length == 0
                    ? 0
                    : exeName == normalizedDirName
                        ? 3
                        : exeName.Contains(normalizedDirName, StringComparison.Ordinal) ||
                          normalizedDirName.Contains(exeName, StringComparison.Ordinal)
                            ? 2
                            : 0;

                found.Add((exe, version, priority));
            }

            if (found.Count == 0)
            {
                // 目录存在但读不到任何 exe 版本：仍把安装目录返回给界面，版本留空由用户手填
                return new DetectedApp
                {
                    DisplayName = string.IsNullOrWhiteSpace(dirName) ? full : dirName,
                    Version = null,
                    InstallLocation = full,
                    DetectedFrom = $"安装目录: {full}（未找到可读版本的 exe）",
                };
            }

            var bestPriority = found.Max(f => f.Priority);
            var best = found
                .Where(f => f.Priority == bestPriority)
                .OrderByDescending(f => f.Version, Comparer<string>.Create(VersionComparer.Compare))
                .First();

            return new DetectedApp
            {
                DisplayName = string.IsNullOrWhiteSpace(dirName) ? Path.GetFileNameWithoutExtension(best.Exe) : dirName,
                Version = best.Version,
                InstallLocation = full,
                DetectedFrom = $"FileVersion: {best.Exe}",
            };
        }
        catch (Exception ex)
        {
            AppLog.Write($"从安装目录识别版本失败：{installDir}", ex);
            return null;
        }
    }

    // ---------------- 打分 ----------------

    private static readonly Regex TrailingArchRegex = new(
        @"\s*\((?:64-bit(?:\s+x64)?|32-bit(?:\s+x86)?|x64|x86|arm64|win64|win32)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrailingVersionRegex = new(
        @"\s*(?:版本|version|ver\.?|v)?\s*\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.\-]+)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static string StripTrailingVersionAndArch(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var value = name.Trim();
        for (var i = 0; i < 2; i++)
        {
            value = TrailingArchRegex.Replace(value, string.Empty).Trim();
            value = TrailingVersionRegex.Replace(value, string.Empty).Trim();
        }

        return value;
    }

    internal static double ScoreCandidate(AppEntry entry, DetectedApp app)
    {
        var rawDisplay = NormalizeName(app.DisplayName);
        if (rawDisplay.Length == 0)
        {
            return 0;
        }

        var baseDisplay = NormalizeName(StripTrailingVersionAndArch(app.DisplayName));
        double score;

        // 若已绑定非别名主程序 EXE，识别到的软件名称必须与绑定的 EXE 软件名一致，否则不显示
        if (!string.IsNullOrWhiteSpace(entry.ExePath) && !IsWindowsAppsAliasPath(entry.ExePath))
        {
            var boundExeName = NormalizeName(Path.GetFileNameWithoutExtension(entry.ExePath));
            if (boundExeName.Length == 0 || (rawDisplay != boundExeName && baseDisplay != boundExeName))
            {
                return 0;
            }

            score = 100d;
        }
        else
        {
            var entryName = NormalizeName(entry.Name);
            var matchesName = entryName.Length > 0 && (rawDisplay == entryName || baseDisplay == entryName);
            var matchesRemark = !string.IsNullOrWhiteSpace(entry.Remark)
                                && (NormalizeName(entry.Remark) == rawDisplay
                                    || NormalizeName(entry.Remark) == baseDisplay
                                    || entry.Remark
                                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Any(line =>
                                        {
                                            var nl = NormalizeName(line);
                                            return nl.Length > 0 && (nl == rawDisplay || nl == baseDisplay);
                                        }));

            if (!matchesName && !matchesRemark)
            {
                return 0;
            }

            score = matchesName ? 100d : 90d;
        }

        if (entry.Source == SourceKind.WinGet)
        {
            // 包 ID 最后一段（如 UURemote）出现在显示名里
            var id = (entry.Package ?? string.Empty).Trim();
            var lastSegment = id.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            var normalizedLast = NormalizeName(lastSegment);
            if (normalizedLast.Length >= 2 && rawDisplay.Contains(normalizedLast, StringComparison.Ordinal))
            {
                score += 40;
            }
        }

        if (entry.Source == SourceKind.GitHub)
        {
            var repo = AssetMatcher.ParseGitHubRepo(entry.RepoUrl);
            if (repo is not null)
            {
                var normalizedRepo = NormalizeName(repo.Value.Repo);
                if (normalizedRepo.Length >= 2 && rawDisplay.Contains(normalizedRepo, StringComparison.Ordinal))
                {
                    score += 40;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.InstallDir) &&
            !string.IsNullOrWhiteSpace(app.InstallLocation) &&
            NormalizePath(entry.InstallDir) == NormalizePath(app.InstallLocation))
        {
            score += 60;
        }

        // 能给出当前版本的候选更值得推荐
        if (!string.IsNullOrWhiteSpace(app.Version))
        {
            score += 10;
        }

        return score;
    }

    /// <summary>把名称归一化：全半角统一、去掉空格与标点、转小写，便于中英文混排比较。</summary>
    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var normalized = name.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    /// <summary>字符二元组 Dice 相似度（0~1），中英文混排也能给出较稳定的比例。</summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (a == b)
        {
            return 1;
        }

        if (a.Length < 2 || b.Length < 2)
        {
            var setA = a.ToHashSet();
            var setB = b.ToHashSet();
            var union = setA.Union(setB).Count();
            return union == 0 ? 0 : (double)setA.Intersect(setB).Count() / union;
        }

        var gramsA = CountBigrams(a);
        var gramsB = CountBigrams(b);
        var total = a.Length - 1 + b.Length - 1;
        var common = 0;
        foreach (var (gram, count) in gramsA)
        {
            if (gramsB.TryGetValue(gram, out var other))
            {
                common += Math.Min(count, other);
            }
        }

        return total == 0 ? 0 : 2d * common / total;
    }

    private static Dictionary<string, int> CountBigrams(string value)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < value.Length - 1; i++)
        {
            var gram = value.Substring(i, 2);
            map[gram] = map.TryGetValue(gram, out var count) ? count + 1 : 1;
        }

        return map;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return string.IsNullOrWhiteSpace(expanded) ? null : expanded.TrimEnd('\\');
    }

    /// <summary>
    /// 清理版本字符串：去掉 .NET/Chromium 资源里常见的 "8,0,3026,36720 @Commit: xxx" 尾巴，
    /// 逗号转点号；这样 FileVersion 也能参与版本比较。
    /// </summary>
    private static string? CleanVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        var at = value.IndexOf('@');
        if (at > 0)
        {
            value = value[..at].Trim();
        }

        value = value.Replace(',', '.').Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? VersionFromExe(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return CleanVersion(info.FileVersion) ?? CleanVersion(info.ProductVersion);
        }
        catch
        {
            return null;
        }
    }

    private static DetectedApp CloneDetected(DetectedApp source) => new()
    {
        DisplayName = source.DisplayName,
        Version = source.Version,
        InstallLocation = source.InstallLocation,
        Publisher = source.Publisher,
        UninstallString = source.UninstallString,
        DisplayIcon = source.DisplayIcon,
        DetectedFrom = source.DetectedFrom,
        Score = source.Score,
    };
}
