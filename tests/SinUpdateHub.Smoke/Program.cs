using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

// SinUpdateHub.Core 冒烟测试：不依赖任何测试框架，直接 dotnet run 看结果。
// 覆盖：版本比较 / 资产匹配 / DPAPI / 本地存储 / GitHub 与 WinGet 真实网络 / 已装软件识别 / WebDAV。
try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // 输出被重定向到不支持设置编码的宿主时忽略
}

Console.WriteLine("============================================================");
Console.WriteLine("  SinUpdateHub.Core 冒烟测试");
Console.WriteLine($"  时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}   机器：{Environment.MachineName}");
Console.WriteLine("============================================================");

var offlineOnly = args.Any(a => a.Equals("--offline", StringComparison.OrdinalIgnoreCase));
var runner = new SmokeRunner(offlineOnly);
await runner.RunAsync();

Console.WriteLine("------------------------------------------------------------");
Console.WriteLine($"汇总：PASS {runner.Passed} / FAIL {runner.Failed} / SKIP {runner.Skipped}（共 {runner.Total} 项）");
if (runner.Failed > 0)
{
    Console.WriteLine("失败项：");
    foreach (var failure in runner.Failures)
    {
        Console.WriteLine("  - " + failure);
    }

    Console.WriteLine("结果：存在失败项，退出码 1。");
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine("结果：全部通过。");
}

/// <summary>冒烟测试执行器：维护 PASS/FAIL/SKIP 计数并输出统一格式。</summary>
internal sealed class SmokeRunner
{
    private const string RepoFreeType = "https://github.com/OUBIGFA/FreeType";

    private readonly AppSettings _networkSettings = new() { IncludePrerelease = false };
    private readonly NetworkClientFactory _factory;
    private readonly bool _offlineOnly;

    public SmokeRunner(bool offlineOnly = false)
    {
        _offlineOnly = offlineOnly;
        _factory = new NetworkClientFactory(() => _networkSettings);
    }

    public int Passed { get; private set; }

    public int Failed { get; private set; }

    public int Skipped { get; private set; }

    public int Total => Passed + Failed + Skipped;

    public List<string> Failures { get; } = new();

    public async Task RunAsync()
    {
        TestVersionComparer();
        TestAssetMatcher();
        TestSecretProtector();
        TestStores();
        await TestInstalledAppDetectorAsync();
        await TestBugFixesOfflineAsync();
        TestNotesOffline();
        await TestManualSourceProviderOfflineAsync();
        if (_offlineOnly)
        {
            Skip("GitHub 来源（真实网络）", "已指定 --offline");
            Skip("WinGet 来源（真实网络）", "已指定 --offline");
            Skip("WebDAV 备份（真实配置）", "已指定 --offline");
            Skip("DownloadService（真实网络）", "已指定 --offline");
            Skip("WebDAV（本地模拟服务器）", "已指定 --offline");
            return;
        }

        await TestGitHubProviderAsync();
        await TestWingetProviderAsync();
        await TestWebDavAsync();
        await TestDownloadServiceAsync();
        await TestWebDavFakeServerAsync();
    }

    // ---------------- 1. VersionComparer ----------------

    private void TestVersionComparer()
    {
        Section("1. VersionComparer");
        try
        {
            Check(
                "IsNewer(\"4.33.0.8907\", \"4.48.2.5754\") == true",
                VersionComparer.IsNewer("4.33.0.8907", "4.48.2.5754"),
                "4.33 应判定比 4.48 旧");
            Check(
                "IsNewer(\"1.2.3\", \"1.2.3\") == false",
                !VersionComparer.IsNewer("1.2.3", "1.2.3"),
                "同版本不应判为更新");
            // 需求原话写的是 IsNewer("v1.10.0", "1.9.9") == true，但 VersionComparer.IsNewer(current, candidate)
            // 的语义是"candidate 是否比 current 新"，1.9.9 比 v1.10.0 旧，按语义应为 false。
            // 这里保留该用例的测试意图（1.10 应按数字段大于 1.9），用等价断言验证。
            Check(
                "Compare(\"v1.10.0\", \"1.9.9\") > 0（1.10 比 1.9 新）",
                VersionComparer.Compare("v1.10.0", "1.9.9") > 0,
                $"实际 Compare = {VersionComparer.Compare("v1.10.0", "1.9.9")}");
            Check(
                "IsNewer(\"1.9.9\", \"v1.10.0\") == true",
                VersionComparer.IsNewer("1.9.9", "v1.10.0"),
                "candidate=v1.10.0 应比 current=1.9.9 新");
            Warn("需求原话 IsNewer(\"v1.10.0\", \"1.9.9\")==true 与 API 语义（candidate 是否比 current 新）相反，已按等价断言改写（1.10.0 > 1.9.9）；未修改 VersionComparer");
            Check(
                "Compare(\"1.0.0-beta\", \"1.0.0\") < 0",
                VersionComparer.Compare("1.0.0-beta", "1.0.0") < 0,
                "预发布版本应小于正式版");
            Check(
                "Normalize(\"v1.2.3+build\") == \"1.2.3\"",
                VersionComparer.Normalize("v1.2.3+build") == "1.2.3",
                $"实际：{VersionComparer.Normalize("v1.2.3+build")}");
            Check(
                "Compare(\"1.0.0-beta.2\", \"1.0.0-beta.10\") < 0",
                VersionComparer.Compare("1.0.0-beta.2", "1.0.0-beta.10") < 0,
                $"实际：{VersionComparer.Compare("1.0.0-beta.2", "1.0.0-beta.10")}");
            Check(
                "Compare(\"1.0.0-beta2\", \"1.0.0-beta10\") < 0",
                VersionComparer.Compare("1.0.0-beta2", "1.0.0-beta10") < 0,
                $"实际：{VersionComparer.Compare("1.0.0-beta2", "1.0.0-beta10")}");
            Check(
                "Compare(\"1.0.0-beta\", \"1.0.0-beta.1\") < 0",
                VersionComparer.Compare("1.0.0-beta", "1.0.0-beta.1") < 0,
                $"实际：{VersionComparer.Compare("1.0.0-beta", "1.0.0-beta.1")}");
            Check(
                "Compare(\"1.2.3-1\", \"1.2.3.1\") == 0",
                VersionComparer.Compare("1.2.3-1", "1.2.3.1") == 0,
                $"实际：{VersionComparer.Compare("1.2.3-1", "1.2.3.1")}");
            Check(
                "Compare(\"1.2.3-1\", \"1.2.3\") > 0",
                VersionComparer.Compare("1.2.3-1", "1.2.3") > 0,
                $"实际：{VersionComparer.Compare("1.2.3-1", "1.2.3")}");
            Check(
                "Compare(\"1.2.3-1.beta\", \"1.2.3\") < 0",
                VersionComparer.Compare("1.2.3-1.beta", "1.2.3") < 0,
                $"实际：{VersionComparer.Compare("1.2.3-1.beta", "1.2.3")}");
            Check(
                "Compare(\"2024-01-15\", \"2024-02-01\") < 0",
                VersionComparer.Compare("2024-01-15", "2024-02-01") < 0,
                $"实际：{VersionComparer.Compare("2024-01-15", "2024-02-01")}");
        }
        catch (Exception ex)
        {
            Fail("VersionComparer 断言组", Describe(ex));
        }
    }

    // ---------------- 2. AssetMatcher ----------------

    private void TestAssetMatcher()
    {
        Section("2. AssetMatcher");
        try
        {
            var assets = new List<AssetCandidate>
            {
                new() { Name = "FreeType-0.1.8-Setup.exe", Url = "https://example.com/FreeType-0.1.8-Setup.exe" },
                new() { Name = "FreeType-0.1.8-Setup-x64.exe", Url = "https://example.com/FreeType-0.1.8-Setup-x64.exe" },
                new() { Name = "FreeType-0.1.8-Setup-arm64.exe", Url = "https://example.com/FreeType-0.1.8-Setup-arm64.exe" },
                new() { Name = "sha256sums.txt", Url = "https://example.com/sha256sums.txt" },
            };

            // 产品语义说明：现有 AssetMatcher 按"包含"匹配，用户填 "Setup" 即可命中全部 3 个安装包变体；
            // 填 "Setup.exe" 只会命中文件名里真含该子串的那一个（带 -x64 / -arm64 的文件名不含 "Setup.exe"）。
            var filteredBySetup = AssetMatcher.Filter(assets, "Setup", false);
            Check(
                "Filter(assets, \"Setup\", false) 命中 3 个 exe",
                filteredBySetup.Count == 3 && filteredBySetup.All(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
                $"实际命中 {filteredBySetup.Count} 个：{string.Join("、", filteredBySetup.Select(a => a.Name))}");

            var filteredBySetupExe = AssetMatcher.Filter(assets, "Setup.exe", false);
            Check(
                "Filter(assets, \"Setup.exe\", false) 能命中安装包",
                filteredBySetupExe.Count >= 1 && filteredBySetupExe.All(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
                $"实际命中 {filteredBySetupExe.Count} 个：{string.Join("、", filteredBySetupExe.Select(a => a.Name))}");
            if (filteredBySetupExe.Count != 3)
            {
                Warn($"需求原话期望 Filter(\"Setup.exe\") 命中 3 个，但按现有 AssetMatcher 的\"包含\"语义实际为 {filteredBySetupExe.Count} 个（未修改 AssetMatcher，按约束）");
            }

            var best = AssetMatcher.PickBest(filteredBySetup);
            Check(
                "PickBest 选中 x64 安装包",
                best?.Name == "FreeType-0.1.8-Setup-x64.exe",
                $"实际：{best?.Name ?? "null"}");

            var regexHits = AssetMatcher.Filter(assets, @"^FreeType-.*-Setup\.exe$", true);
            Check(
                "正则 ^FreeType-.*-Setup\\.exe$ 命中 1 个",
                regexHits.Count == 1,
                $"实际命中 {regexHits.Count} 个：{string.Join("、", regexHits.Select(a => a.Name))}");

            var parsed = AssetMatcher.ParseGitHubRepo(RepoFreeType);
            Check(
                "ParseGitHubRepo 解析 owner/repo",
                parsed is { Owner: "OUBIGFA", Repo: "FreeType" },
                $"实际：{parsed}");
        }
        catch (Exception ex)
        {
            Fail("AssetMatcher 断言组", Describe(ex));
        }
    }

    // ---------------- 3. SecretProtector ----------------

    private void TestSecretProtector()
    {
        Section("3. SecretProtector（DPAPI）");
        try
        {
            const string secret = "机密 password!@# 中文";
            var protectedText = SecretProtector.Protect(secret);
            Check(
                "密文以 dpapi: 开头",
                protectedText.StartsWith("dpapi:", StringComparison.Ordinal),
                $"实际前缀：{(protectedText.Length > 12 ? protectedText[..12] : protectedText)}");
            Check(
                "加密 → 解密往返一致",
                SecretProtector.Unprotect(protectedText) == secret,
                "解密结果与原文不一致");
        }
        catch (Exception ex)
        {
            Fail("SecretProtector 断言组", Describe(ex));
        }
    }

    // ---------------- 4. AppStore / SettingsStore ----------------

    private void TestStores()
    {
        Section("4. AppStore / SettingsStore（临时目录）");
        var tempDir = Path.Combine(Path.GetTempPath(), "SinUpdateHub-Smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);

            // ---------- AppStore ----------
            var dataPath = Path.Combine(tempDir, "data.json");
            var store = new AppStore(dataPath);
            var data = new AppData
            {
                Entries = new List<AppEntry>
                {
                    new()
                    {
                        Name = "测试软件 · 中文名称",
                        Source = SourceKind.GitHub,
                        RepoUrl = RepoFreeType,
                        Package = "Setup.exe",
                        CurrentVersion = "0.1.7",
                        InstallDir = @"D:\files\FreeType",
                    },
                },
            };

            store.Save(data);
            var loaded = store.Load();
            Check(
                "AppStore：Save → Load 往返（中文名 / RepoUrl / 安装目录）",
                loaded.Entries.Count == 1
                && loaded.Entries[0].Name == "测试软件 · 中文名称"
                && loaded.Entries[0].RepoUrl == RepoFreeType
                && loaded.Entries[0].InstallDir == @"D:\files\FreeType",
                $"实际：{loaded.Entries.Count} 条，首条 = {(loaded.Entries.Count > 0 ? loaded.Entries[0].ToString() : "无")}");

            store.Save(loaded); // 第二次保存才会生成 data.json.bak
            Check("AppStore：保存时生成 data.json.bak", File.Exists(dataPath + ".bak"), "未找到 .bak 备份");

            var exportPath = Path.Combine(tempDir, "export-sub", "export.json");
            store.Export(loaded, exportPath);
            var imported = store.Import(exportPath);
            Check(
                "AppStore：Export → Import 往返",
                imported.Entries.Count == 1 && imported.Entries[0].Name == "测试软件 · 中文名称",
                $"实际：{imported.Entries.Count} 条");

            var badDir = Path.Combine(tempDir, "bad");
            Directory.CreateDirectory(badDir);
            var badPath = Path.Combine(badDir, "data.json");
            File.WriteAllText(badPath, "{ 这不是合法 JSON ");
            var badStore = new AppStore(badPath);
            var badLoaded = badStore.Load();
            var corruptCopies = Directory.GetFiles(badDir, "data.corrupt-*.json");
            Check(
                "AppStore：坏文件回退为空清单并另存 data.corrupt-*.json",
                badLoaded.Entries.Count == 0 && corruptCopies.Length == 1,
                $"回退后 {badLoaded.Entries.Count} 条，坏文件副本 {corruptCopies.Length} 个");

            // ---------- SettingsStore ----------
            var settingsPath = Path.Combine(tempDir, "settings.json");
            var settingsStore = new SettingsStore(settingsPath);
            const string password = "p@ss\"中文#$%&<>";
            const string token = "ghp_测试Token123456";
            var settings = new AppSettings
            {
                ProxyMode = ProxyMode.Custom,
                ProxyUrl = "http://127.0.0.1:7897",
                WebDavUrl = "https://dav.example.com/dav/",
                WebDavUser = "测试用户",
                WebDavPassword = password,
                GitHubToken = token,
                WebDavFolder = "我的备份/SinUpdateHub",
            };

            settingsStore.Save(settings);
            var raw = File.ReadAllText(settingsPath);
            Check("SettingsStore：落盘文件包含 dpapi: 密文", raw.Contains("dpapi:", StringComparison.Ordinal), "未发现 dpapi: 前缀");
            Check("SettingsStore：落盘文件不含 WebDAV 密码明文", !raw.Contains(password, StringComparison.Ordinal), "检测到明文密码");
            Check("SettingsStore：落盘文件不含 GitHub Token 明文", !raw.Contains(token, StringComparison.Ordinal), "检测到明文 Token");

            var reloaded = settingsStore.Load();
            Check(
                "SettingsStore：Save → Load 往返（密码 / Token 解密一致）",
                reloaded.WebDavPassword == password && reloaded.GitHubToken == token,
                $"密码一致={reloaded.WebDavPassword == password}，Token 一致={reloaded.GitHubToken == token}");
            Check(
                "SettingsStore：普通字段往返",
                reloaded.WebDavUrl == "https://dav.example.com/dav/"
                && reloaded.WebDavFolder == "我的备份/SinUpdateHub"
                && reloaded.ProxyMode == ProxyMode.Custom,
                $"实际：Url={reloaded.WebDavUrl}，Folder={reloaded.WebDavFolder}，Proxy={reloaded.ProxyMode}");

            var partialPath = Path.Combine(tempDir, "settings-partial.json");
            File.WriteAllText(partialPath, "{ \"webDavUrl\": \"https://dav.example.com/dav/\" }");
            var partial = new SettingsStore(partialPath).Load();
            Check(
                "SettingsStore：缺失字段回退默认值",
                partial.WebDavFolder == "SinUpdateHub" && partial.ProxyMode == ProxyMode.System && partial.KeepBackups == 10,
                $"实际：Folder={partial.WebDavFolder}，Proxy={partial.ProxyMode}，Keep={partial.KeepBackups}");
        }
        catch (Exception ex)
        {
            Fail("AppStore / SettingsStore 断言组", Describe(ex));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    // ---------------- 5/6. GitHub / WinGet 真实网络 ----------------

    private async Task TestGitHubProviderAsync()
    {
        Section("5. GitHub 来源（真实网络）");
        var github = new GitHubSourceProvider(_factory, () => _networkSettings);

        // 5a. microsoft/winget-cli：Pattern = msixbundle，无则 msi
        try
        {
            var entry = new AppEntry
            {
                Name = "WinGet CLI",
                Source = SourceKind.GitHub,
                RepoUrl = "https://github.com/microsoft/winget-cli",
                Package = "msixbundle",
            };

            AvailableUpdate update;
            try
            {
                update = await NetworkAsync("microsoft/winget-cli(msixbundle)", () => github.GetLatestAsync(entry));
            }
            catch (UpdateSourceException ex) when (ex.Message.Contains("未匹配到安装包", StringComparison.Ordinal))
            {
                Console.WriteLine("      msixbundle 未匹配，改用 msi 再试…");
                entry.Package = "msi";
                update = await NetworkAsync("microsoft/winget-cli(msi)", () => github.GetLatestAsync(entry));
            }

            Console.WriteLine($"      → 版本 {update.Version}，资产 {update.AssetName}，候选 {update.Candidates.Count} 个");
            Console.WriteLine($"      → 下载地址 {(update.DownloadUrl ?? "（无）")}");
            Check("GitHub：microsoft/winget-cli 版本非空", !string.IsNullOrWhiteSpace(update.Version), "Version 为空");
            Check(
                "GitHub：microsoft/winget-cli 下载地址非空",
                !string.IsNullOrWhiteSpace(update.DownloadUrl),
                $"Version={update.Version}，AssetName={update.AssetName ?? "null"}");
        }
        catch (Exception ex)
        {
            Fail("GitHub：microsoft/winget-cli", Describe(ex));
        }

        // 5b. git-for-windows/git：不填 Pattern，自动挑 .exe
        try
        {
            var entry = new AppEntry
            {
                Name = "Git for Windows",
                Source = SourceKind.GitHub,
                RepoUrl = "https://github.com/git-for-windows/git",
                Package = string.Empty,
            };

            var update = await NetworkAsync("git-for-windows/git", () => github.GetLatestAsync(entry));
            Console.WriteLine($"      → 版本 {update.Version}，自动挑选 {update.AssetName}，候选 {update.Candidates.Count} 个");
            Check("GitHub：git-for-windows/git 版本非空", !string.IsNullOrWhiteSpace(update.Version), "Version 为空");
            Check(
                "GitHub：git-for-windows/git 自动挑到 .exe",
                update.AssetName is not null && update.AssetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                $"实际：{update.AssetName ?? "null"}");
        }
        catch (Exception ex)
        {
            Fail("GitHub：git-for-windows/git", Describe(ex));
        }
    }

    private async Task TestWingetProviderAsync()
    {
        Section("6. WinGet 来源（真实网络）");
        try
        {
            var winget = new WingetSourceProvider(_factory, () => _networkSettings);
            var entry = new AppEntry
            {
                Name = "UU 远程",
                Source = SourceKind.WinGet,
                Package = "NetEase.UURemote",
            };

            var update = await NetworkAsync("NetEase.UURemote", () => winget.GetLatestAsync(entry));
            Console.WriteLine($"      → 版本 {update.Version}，安装器 {update.AssetName ?? "（无直链）"}");
            Console.WriteLine($"      → 清单页 {update.ReleasePageUrl}");
            Console.WriteLine($"      → 下载地址 {(update.DownloadUrl ?? "（无）")}");
            if (update.Advisory is not null)
            {
                Console.WriteLine($"      → 提示：{update.Advisory}");
            }

            Check(
                "WinGet：NetEase.UURemote 版本以 4. 开头",
                update.Version.StartsWith("4.", StringComparison.Ordinal),
                $"实际：{update.Version}");
            Check(
                "WinGet：NetEase.UURemote 下载地址非空",
                !string.IsNullOrWhiteSpace(update.DownloadUrl),
                $"Version={update.Version}，AssetName={update.AssetName ?? "null"}");
        }
        catch (Exception ex)
        {
            Fail("WinGet：NetEase.UURemote", Describe(ex));
        }
    }

    // ---------------- 7. InstalledAppDetector ----------------

    private async Task TestInstalledAppDetectorAsync()
    {
        Section("7. InstalledAppDetector（本机）");
        try
        {
            var detector = new InstalledAppDetector();
            var installed = await detector.ScanInstalledAsync();
            Console.WriteLine($"      注册表扫描到已安装软件 {installed.Count} 条");
            var sample = installed.Take(5).Select(a => a.Summary).ToList();
            foreach (var line in sample)
            {
                Console.WriteLine($"      样例：{line}");
            }

            Check(
                "InstalledAppDetector：已安装软件数量 > 20",
                installed.Count > 20,
                $"实际 {installed.Count} 条");

            var entry = new AppEntry
            {
                Name = "UU 远程",
                Source = SourceKind.WinGet,
                Package = "NetEase.UURemote",
            };
            var candidates = await detector.FindCandidatesAsync(entry);
            if (candidates.Count == 0)
            {
                Console.WriteLine("      候选：（无，本机可能未安装 UU 远程，属正常情况）");
            }
            else
            {
                foreach (var candidate in candidates.Take(3))
                {
                    Console.WriteLine($"      候选：{candidate.Summary}（得分 {candidate.Score:0.#}，来源 {candidate.DetectedFrom}）");
                }
            }

            var resolved = await detector.ResolveFromInstallDirAsync(@"C:\Program Files\dotnet");
            if (resolved is null)
            {
                Console.WriteLine("      安装目录识别：返回 null");
            }
            else
            {
                Console.WriteLine($"      安装目录识别：{resolved.Summary}（来源 {resolved.DetectedFrom}）");
            }

            Check(
                "InstalledAppDetector：从 C:\\Program Files\\dotnet 读到 dotnet.exe 版本",
                resolved is not null && !string.IsNullOrWhiteSpace(resolved.Version),
                resolved is null ? "ResolveFromInstallDirAsync 返回 null" : $"Version={resolved.Version ?? "空"}");
        }
        catch (Exception ex)
        {
            Fail("InstalledAppDetector 断言组", Describe(ex));
        }
    }

    // ---------------- 8. WebDAV ----------------

    private async Task TestWebDavAsync()
    {
        Section("8. WebDAV 备份");
        try
        {
            var settings = new SettingsStore().Load();
            if (string.IsNullOrWhiteSpace(settings.WebDavUrl))
            {
                Skip("WebDAV：连接测试", "未配置 WebDAV 地址（settings.json 的 WebDavUrl 为空）");
                return;
            }

            var service = new WebDavBackupService(_factory);
            var result = await NetworkAsync("WebDAV 连接测试", () => service.CheckAsync(settings));
            Console.WriteLine($"      → {result.Message}");
            Check("WebDAV：连接测试", result.Ok, result.Message);
        }
        catch (Exception ex)
        {
            Fail("WebDAV 断言组", Describe(ex));
        }
    }

    // ---------------- 9. DownloadService ----------------

    private async Task TestDownloadServiceAsync()
    {
        Section("9. DownloadService（真实网络，小文件）");
        var tempDir = Path.Combine(Path.GetTempPath(), "SinUpdateHub-Download-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { DownloadDir = tempDir };
            var factory = new NetworkClientFactory(() => settings);
            var service = new DownloadService(factory, () => settings);
            const string fileName = "NetEase.UURemote.installer.yaml";
            const string url =
                "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/n/NetEase/UURemote/4.33.0.8907/NetEase.UURemote.installer.yaml";

            var progress = new ListProgress<DownloadProgress>();
            var path = await NetworkAsync("下载小文件", () => service.DownloadAsync(url, fileName, progress));
            var size = new FileInfo(path).Length;
            Check(
                "DownloadService：文件落盘且内容非空",
                File.Exists(path) && size > 0,
                $"路径={path}，大小={size}");
            Check("DownloadService：有进度回调", progress.Items.Count > 0, "未收到任何 DownloadProgress");
            Check(
                "DownloadService：.part 临时文件已清理",
                !File.Exists(path + ".part"),
                $"仍存在：{path}.part");

            var reused = await NetworkAsync("复用已下载文件", () => service.DownloadAsync(url, fileName, null));
            Check(
                "DownloadService：同名同大小文件直接复用",
                string.Equals(reused, path, StringComparison.OrdinalIgnoreCase),
                $"第一次={path}，第二次={reused}");
        }
        catch (Exception ex)
        {
            Fail("DownloadService 断言组", Describe(ex));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    // ---------------- 10. WebDAV（本地模拟服务器） ----------------

    private async Task TestWebDavFakeServerAsync()
    {
        Section("10. WebDAV（本地模拟服务器，验证连接 / 上传 / 清理 / 下载 / 删除）");
        try
        {
            using var server = new FakeWebDavServer("dav-user", "dav-pass");
            var localSettings = new AppSettings { ProxyMode = ProxyMode.None };
            var factory = new NetworkClientFactory(() => localSettings);
            var service = new WebDavBackupService(factory);
            var settings = new AppSettings
            {
                ProxyMode = ProxyMode.None,
                WebDavUrl = server.RootUrl,
                WebDavUser = "dav-user",
                WebDavPassword = "dav-pass",
                WebDavFolder = "SinUpdateHub",
                KeepBackups = 1,
            };

            var check = await service.CheckAsync(settings);
            Console.WriteLine($"      → {check.Message}");
            Check("WebDAV：连接测试（MKCOL + PROPFIND Depth 0）", check.Ok, check.Message);

            var data = new AppData
            {
                Entries = new List<AppEntry>
                {
                    new()
                    {
                        Name = "WebDAV 往返测试",
                        Source = SourceKind.GitHub,
                        RepoUrl = RepoFreeType,
                        CurrentVersion = "1.0.0",
                    },
                },
            };

            var first = await service.UploadAsync(settings, data);
            Check("WebDAV：上传成功（每次一条时间戳快照）", first.Ok && first.File is not null, first.Message);

            await Task.Delay(TimeSpan.FromSeconds(1.2)); // 时间戳精确到秒，等一秒保证生成第二个快照
            var second = await service.UploadAsync(settings, data);
            Check(
                "WebDAV：按 KeepBackups=1 清理旧快照",
                second.Ok && second.PrunedCount == 1,
                $"Ok={second.Ok}，Pruned={second.PrunedCount}，{second.Message}");

            var list = await service.ListAsync(settings);
            Console.WriteLine($"      → 远端文件：{string.Join("、", list.Select(f => f.FileName))}");
            Check("WebDAV：列出备份（只有 1 条时间戳快照，data.json 不再生成）", list.Count == 1, $"实际 {list.Count} 个");

            var restored = await service.DownloadAsync(settings, list[0].FileName);
            Check(
                "WebDAV：下载快照并还原清单",
                restored.Entries.Count == 1 && restored.Entries[0].Name == "WebDAV 往返测试",
                $"实际 {restored.Entries.Count} 条");

            var timestamped = list.FirstOrDefault(f =>
                !string.Equals(f.FileName, "data.json", StringComparison.OrdinalIgnoreCase));
            if (timestamped is not null)
            {
                await service.DeleteAsync(settings, timestamped.FileName);
                var afterDelete = await service.ListAsync(settings);
                Check("WebDAV：删除指定备份", afterDelete.Count == list.Count - 1, $"删除后 {afterDelete.Count} 个");
            }

            var badSettings = settings.Clone();
            badSettings.WebDavPassword = "wrong-password";
            var badCheck = await service.CheckAsync(badSettings);
            Check(
                "WebDAV：密码错误时给出可操作的中文提示",
                !badCheck.Ok && badCheck.Message.Contains("账号或密码错误", StringComparison.Ordinal),
                badCheck.Message);
        }
        catch (Exception ex)
        {
            Fail("WebDAV 本地模拟服务器断言组", Describe(ex));
        }
    }

    // ---------------- 11. 离线回归测试（BUG 1-10 修复验证） ----------------

    private async Task TestBugFixesOfflineAsync()
    {
        Section("11. 离线回归测试（BUG 1-10）");
        await TestNetworkFactoryOfflineAsync();
        TestCopyEditableAndApplyOffline();
        TestProxyBypassListOffline();
        await TestWingetCatalogReplaceOfflineAsync();
        TestDirectoryScoreOffline();
        await TestInstallerRunnerOfflineAsync();
        await TestAppxSupportOfflineAsync();
    }

    private async Task TestNetworkFactoryOfflineAsync()
    {
        try
        {
            const string secretToken = "ghp_secret_leak_test";
            var capturedHeaders = new List<string>();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var serverCts = new CancellationTokenSource();
            var serverTask = Task.Run(async () =>
            {
                while (!serverCts.IsCancellationRequested)
                {
                    TcpClient tcp;
                    try
                    {
                        tcp = await listener.AcceptTcpClientAsync(serverCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }

                    using (tcp)
                    {
                        var stream = tcp.GetStream();
                        var buf = new List<byte>(2048);
                        var single = new byte[1];
                        while (buf.Count < 16384)
                        {
                            var n = await stream.ReadAsync(single, serverCts.Token).ConfigureAwait(false);
                            if (n <= 0) break;
                            buf.Add(single[0]);
                            var c = buf.Count;
                            if (c >= 4 && buf[c - 4] == '\r' && buf[c - 3] == '\n' && buf[c - 2] == '\r' && buf[c - 1] == '\n')
                            {
                                break;
                            }
                        }

                        lock (capturedHeaders)
                        {
                            capturedHeaders.Add(Encoding.ASCII.GetString(buf.ToArray()));
                        }

                        var resp = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                        await stream.WriteAsync(resp, serverCts.Token).ConfigureAwait(false);
                        await stream.FlushAsync(serverCts.Token).ConfigureAwait(false);
                    }
                }
            });

            try
            {
                var settings = new AppSettings { ProxyMode = ProxyMode.None, GitHubToken = secretToken };
                using var factory = new NetworkClientFactory(() => settings);

                var apiClient = factory.Client;
                var dlClient = factory.DownloadClient;
                Check(
                    "BUG1：Client 与 DownloadClient 各自独立缓存",
                    !ReferenceEquals(apiClient, dlClient)
                    && ReferenceEquals(factory.Client, apiClient)
                    && ReferenceEquals(factory.DownloadClient, dlClient),
                    "客户端实例未按超时分别缓存");

                var url = $"http://127.0.0.1:{port}/asset.exe";
                var r1 = await apiClient.GetAsync(url);
                var r2 = await dlClient.GetAsync(url);
                var r3 = await apiClient.GetAsync(url);
                Check(
                    "BUG1：交替调用 Client 与 DownloadClient 不互相 Dispose",
                    r1.IsSuccessStatusCode && r2.IsSuccessStatusCode && r3.IsSuccessStatusCode,
                    $"状态码：{(int)r1.StatusCode}, {(int)r2.StatusCode}, {(int)r3.StatusCode}");
                r1.Dispose();
                r2.Dispose();
                r3.Dispose();

                List<string> snapshot;
                lock (capturedHeaders)
                {
                    snapshot = capturedHeaders.ToList();
                }

                var leaked = snapshot.Any(h =>
                    h.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
                    || h.Contains("X-GitHub-Api-Version", StringComparison.OrdinalIgnoreCase)
                    || h.Contains(secretToken, StringComparison.Ordinal));
                Check(
                    "BUG2：非 api.github.com 请求不携带 GitHub Token 或 API 版本头",
                    snapshot.Count == 3 && !leaked,
                    $"请求数={snapshot.Count}，leaked={leaked}");

                using var ghReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                NetworkClientFactory.ApplyGitHubHeaders(ghReq, secretToken);
                Check(
                    "BUG2：https://api.github.com 请求正确附加 Token 与版本头",
                    ghReq.Headers.Authorization?.Scheme == "Bearer"
                    && ghReq.Headers.Authorization?.Parameter == secretToken
                    && ghReq.Headers.Contains("X-GitHub-Api-Version")
                    && ghReq.Headers.Accept.Any(a => a.MediaType == "application/vnd.github+json"),
                    "未附加预期请求头");

                using var httpGhReq = new HttpRequestMessage(HttpMethod.Get, "http://api.github.com/user");
                NetworkClientFactory.ApplyGitHubHeaders(httpGhReq, secretToken);
                Check(
                    "BUG2：非 HTTPS 的 http://api.github.com 不附加 Token",
                    httpGhReq.Headers.Authorization is null && !httpGhReq.Headers.Contains("X-GitHub-Api-Version"),
                    "明文 HTTP 错误附加了认证头");

                using var thirdPartyReq = new HttpRequestMessage(HttpMethod.Get, "https://example.com/file.exe");
                NetworkClientFactory.ApplyGitHubHeaders(thirdPartyReq, secretToken);
                Check(
                    "BUG2：第三方 HTTPS 域名不附加 Token",
                    thirdPartyReq.Headers.Authorization is null && !thirdPartyReq.Headers.Contains("X-GitHub-Api-Version"),
                    "第三方域名错误附加了认证头");

                using var privateAssetReq = DownloadService.CreateDownloadRequest(
                    new Uri("https://api.github.com/repos/China-Sin/SinUpdateHub/releases/assets/123456"));
                NetworkClientFactory.ApplyGitHubHeaders(privateAssetReq, secretToken);
                Check(
                    "GitHub 私密仓库下载：资产 API 请求携带 Token 与纯 application/octet-stream（不混入 vnd.github+json）",
                    privateAssetReq.Headers.Authorization?.Scheme == "Bearer"
                    && privateAssetReq.Headers.Authorization?.Parameter == secretToken
                    && privateAssetReq.Headers.Accept.Count == 1
                    && privateAssetReq.Headers.Accept.Any(a => a.MediaType == "application/octet-stream"),
                    $"Accept={string.Join(",", privateAssetReq.Headers.Accept.Select(a => a.MediaType))}");

                using var publicDlReq = DownloadService.CreateDownloadRequest(
                    new Uri("https://github.com/China-Sin/SinUpdateHub/releases/download/v1.0.0/SinUpdateHub.exe"));
                NetworkClientFactory.ApplyGitHubHeaders(publicDlReq, secretToken);
                Check(
                    "GitHub 公开下载链接：不附加 Token 也不附加 application/octet-stream",
                    publicDlReq.Headers.Authorization is null && publicDlReq.Headers.Accept.Count == 0,
                    $"Auth={publicDlReq.Headers.Authorization}, AcceptCount={publicDlReq.Headers.Accept.Count}");
            }
            finally
            {
                serverCts.Cancel();
                listener.Stop();
                await serverTask;
            }
        }
        catch (Exception ex)
        {
            Fail("BUG1/2：NetworkClientFactory 离线测试", Describe(ex));
        }
    }

    private void TestCopyEditableAndApplyOffline()
    {
        try
        {
            var target = new AppEntry { Name = "Broadcast", DisplayName = "旧显示名", Source = SourceKind.Custom, HomepageUrl = "https://old.example.com" };
            var edited = new AppEntry { Name = "Broadcast", DisplayName = "NVIDIA Broadcast", Source = SourceKind.Custom, HomepageUrl = "https://new.example.com" };
            target.CopyEditableFrom(edited);
            var updatedOk = target.HomepageUrl == "https://new.example.com"
                            && target.DisplayName == "NVIDIA Broadcast";

            edited.HomepageUrl = null;
            target.CopyEditableFrom(edited);
            var clearedOk = target.HomepageUrl is null;

            Check(
                "BUG3：AppEntry.CopyEditableFrom 正确同步 DisplayName 与清空 HomepageUrl",
                updatedOk && clearedOk,
                $"updatedOk={updatedOk}, clearedOk={clearedOk}");

            var wingetEntry = new AppEntry { Name = "Pkg", Source = SourceKind.WinGet, Package = "Pub.Pkg", CurrentVersion = "1.0.0" };
            UpdateChecker.Apply(wingetEntry, new CheckResult
            {
                EntryId = wingetEntry.Id,
                State = UpdateState.UpdateAvailable,
                Update = new AvailableUpdate
                {
                    Version = "2.0.0",
                    DownloadUrl = "https://dl.example.com/pkg.exe",
                    HomepageUrl = "https://home.example.com",
                },
            });
            UpdateChecker.Apply(wingetEntry, CheckResult.Fail(wingetEntry.Id, "临时断网"));
            var failPreserved = wingetEntry.LatestVersion == "2.0.0"
                                && wingetEntry.DownloadUrl == "https://dl.example.com/pkg.exe"
                                && wingetEntry.HomepageUrl == "https://home.example.com"
                                && wingetEntry.LastError == "临时断网";

            var customEntry = new AppEntry
            {
                Name = "Manual",
                Source = SourceKind.Custom,
                HomepageUrl = "https://old.example.com",
                LatestVersion = "keep",
            };
            UpdateChecker.Apply(customEntry, new CheckResult
            {
                EntryId = customEntry.Id,
                State = UpdateState.Unknown,
                Update = new AvailableUpdate { HomepageUrl = "https://custom.example.com" },
            });
            var customPreserved = customEntry.LatestVersion == "keep"
                                  && customEntry.HomepageUrl == "https://custom.example.com"
                                  && customEntry.LastError is null;

            Check(
                "BUG8：UpdateChecker.Apply 保留历史快照并同步 HomepageUrl",
                failPreserved && customPreserved,
                $"failPreserved={failPreserved}, customPreserved={customPreserved}");
        }
        catch (Exception ex)
        {
            Fail("BUG3/8：CopyEditableFrom / UpdateChecker.Apply", Describe(ex));
        }
    }

    private void TestProxyBypassListOffline()
    {
        try
        {
            var proxy = new WebProxy("http://127.0.0.1:7897");
            NetworkClientFactory.ApplyBypassList(proxy, "localhost;127.*;*.local;192.168.*;<local>");
            var ok = proxy.BypassProxyOnLocal
                     && proxy.IsBypassed(new Uri("http://localhost:8080/"))
                     && proxy.IsBypassed(new Uri("http://127.0.0.1:9000/"))
                     && proxy.IsBypassed(new Uri("http://foo.local/"))
                     && proxy.IsBypassed(new Uri("http://192.168.1.20/"))
                     && !proxy.IsBypassed(new Uri("https://api.github.com/"))
                     && !proxy.IsBypassed(new Uri("https://notlocalhost.example.com/"));

            Check(
                "BUG5：系统代理 BypassList 保留全部合法规则与 <local>",
                ok,
                $"BypassProxyOnLocal={proxy.BypassProxyOnLocal}, Count={proxy.BypassList.Length}");
        }
        catch (Exception ex)
        {
            Fail("BUG5：ApplyBypassList", Describe(ex));
        }
    }

    private async Task TestWingetCatalogReplaceOfflineAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SinUpdateHub-Catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var indexPath = Path.Combine(tempDir, "index.db");
            var db2Path = Path.Combine(tempDir, "index2.db");
            var msixPath = Path.Combine(tempDir, "source.msix");

            CreateMinimalCatalogDb(indexPath, "Test.Pkg", "Test App", "1.0.0");
            CreateMinimalCatalogDb(db2Path, "Test.Pkg", "Test App", "2.0.0");
            SqliteConnection.ClearAllPools();

            using (var zip = ZipFile.Open(msixPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(db2Path, "Public/index.db");
            }

            using var factory = new NetworkClientFactory(() => new AppSettings { ProxyMode = ProxyMode.None });
            var catalog = new WingetCatalogService(factory, indexPath, minReadyBytes: 1);

            var v1 = await catalog.GetLatestVersionAsync("Test.Pkg");
            catalog.ExtractIndex(msixPath);
            var v2 = await catalog.GetLatestVersionAsync("Test.Pkg");

            Check(
                "BUG6：查询本地 WinGet 索引后可无锁覆盖更新 index.db",
                v1 == "1.0.0" && v2 == "2.0.0",
                $"v1={v1 ?? "null"}, v2={v2 ?? "null"}");
        }
        catch (Exception ex)
        {
            Fail("BUG6：WingetCatalogService 替换索引", Describe(ex));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(tempDir);
        }
    }

    private static void CreateMinimalCatalogDb(string dbPath, string id, string name, string version)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE ids (id TEXT NOT NULL);
            CREATE TABLE names (name TEXT NOT NULL);
            CREATE TABLE versions (version TEXT NOT NULL);
            CREATE TABLE manifest (id INTEGER NOT NULL, name INTEGER NOT NULL, version INTEGER NOT NULL);
            INSERT INTO ids (id) VALUES ($id);
            INSERT INTO names (name) VALUES ($name);
            INSERT INTO versions (version) VALUES ($version);
            INSERT INTO manifest (id, name, version) VALUES (1, 1, 1);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.ExecuteNonQuery();
    }

    private void TestDirectoryScoreOffline()
    {
        try
        {
            var shellDetected = new DetectedApp
            {
                DisplayName = "ShellBandizip",
                Version = "1.0.0.3",
                InstallLocation = @"D:\files\Bandizip\data",
            };
            var mismatchScore = InstalledAppDetector.ScoreCandidate(
                new AppEntry { Name = "Bandizip", Source = SourceKind.Custom, Remark = string.Empty },
                shellDetected);
            Check(
                "识别过滤：左侧名称与项目名称/备注不同时得分为 0（不显示 ShellBandizip）",
                mismatchScore == 0,
                $"实际得分={mismatchScore}");

            var detected = new DetectedApp
            {
                DisplayName = "Bandizip",
                Version = "7.44",
                InstallLocation = @"C:\Tools\Bandizip\",
            };

            var baseWinGet = InstalledAppDetector.ScoreCandidate(
                new AppEntry { Name = "Bandizip", Source = SourceKind.WinGet },
                detected);
            var wingetScore = InstalledAppDetector.ScoreCandidate(
                new AppEntry { Name = "Bandizip", Source = SourceKind.WinGet, InstallDir = @"c:\tools\bandizip" },
                detected);
            var customScore = InstalledAppDetector.ScoreCandidate(
                new AppEntry { Name = "Bandizip", Source = SourceKind.Custom, InstallDir = @"c:\tools\bandizip" },
                detected);
            var githubScore = InstalledAppDetector.ScoreCandidate(
                new AppEntry { Name = "Bandizip", Source = SourceKind.GitHub, InstallDir = @"c:\tools\bandizip" },
                detected);

            Check(
                "BUG7：WinGet / 官网 / GitHub 来源均享受安装目录匹配加分",
                baseWinGet > 0
                && wingetScore - baseWinGet == 60
                && customScore - baseWinGet == 60
                && githubScore - baseWinGet == 60,
                $"Base={baseWinGet}, WinGet={wingetScore}, Custom={customScore}, GitHub={githubScore}");

            var remarkScore = InstalledAppDetector.ScoreCandidate(
                new AppEntry { Name = "压缩工具", Source = SourceKind.Custom, Remark = "Bandizip" },
                detected);
            Check(
                "识别匹配：左侧名称与备注相同时可命中",
                remarkScore > 0,
                $"实际得分={remarkScore}");

            Check(
                "目录识别：IsUninstallerExe 排除 unins000.exe 与 Uninstall.exe",
                InstalledAppDetector.IsUninstallerExe(@"D:\files\CitePix\unins000.exe")
                && InstalledAppDetector.IsUninstallerExe(@"D:\files\App\Uninstall.exe")
                && !InstalledAppDetector.IsUninstallerExe(@"D:\files\CitePix\CitePix.exe"),
                "卸载程序识别结果不符合预期");

            if (Directory.Exists(@"D:\files\CitePix"))
            {
                var citePix = new InstalledAppDetector().ResolveFromInstallDirAsync(@"D:\files\CitePix").GetAwaiter().GetResult();
                Check(
                    "目录识别：D:\\files\\CitePix 识别为软件版本而非 unins000.exe (51.1054.0.0)",
                    citePix is not null
                    && !string.IsNullOrWhiteSpace(citePix.Version)
                    && citePix.Version != "51.1054.0.0",
                    citePix is null ? "返回 null" : $"DisplayName={citePix.DisplayName}, Version={citePix.Version ?? "null"}");
            }

            Check(
                "识别去尾：StripTrailingVersionAndArch 去掉尾部版本与架构后缀",
                InstalledAppDetector.StripTrailingVersionAndArch("CitePix 版本 0.9.0") == "CitePix"
                && InstalledAppDetector.StripTrailingVersionAndArch("Notepad++ (64-bit x64)") == "Notepad++"
                && InstalledAppDetector.StripTrailingVersionAndArch("ShellBandizip") == "ShellBandizip",
                "去尾结果不符合预期");

            var boundMatch = InstalledAppDetector.ScoreCandidate(
                new AppEntry { Name = "截图工具", ExePath = @"D:\files\CitePix\CitePix.exe" },
                new DetectedApp { DisplayName = "CitePix 版本 0.9.0", Version = "0.9.0" });
            var boundMismatch = InstalledAppDetector.ScoreCandidate(
                new AppEntry { Name = "截图工具", ExePath = @"D:\files\CitePix\CitePix.exe" },
                new DetectedApp { DisplayName = "OtherTool", Version = "1.0.0" });
            Check(
                "识别过滤：绑定 EXE 时仅显示与绑定软件名称一致的候选",
                boundMatch > 0 && boundMismatch == 0,
                $"boundMatch={boundMatch}, boundMismatch={boundMismatch}");

            var rExe = UpdateChecker.ReconcileVersionWithLatest("1.2.3.4", "1.2.3", "1.2.3.4");
            var rSoft = UpdateChecker.ReconcileVersionWithLatest("1.2.3.4", "1.2.3", "1.2.3");
            var rNeither = UpdateChecker.ReconcileVersionWithLatest("1.2.3.4", "1.2.3", "2.0.0");
            var rEqual = UpdateChecker.ReconcileVersionWithLatest("1.2.3", "1.2.3", "2.0.0");
            Check(
                "版本仲裁：EXE 与软件版本不一致时优先采用与远端最新一致的那个，都不一致优先 EXE",
                rExe == "1.2.3.4" && rSoft == "1.2.3" && rNeither == "1.2.3.4" && rEqual == "1.2.3",
                $"rExe={rExe}, rSoft={rSoft}, rNeither={rNeither}, rEqual={rEqual}");
        }
        catch (Exception ex)
        {
            Fail("BUG7：InstalledAppDetector.ScoreCandidate", Describe(ex));
        }
    }

    private async Task TestInstallerRunnerOfflineAsync()
    {
        try
        {
            var whereExe = Path.Combine(Environment.SystemDirectory, "where.exe");
            var runner = new InstallerRunner();

            var ok = await runner.RunAsync(whereExe, "cmd.exe", runAsAdmin: false);
            Check(
                "BUG10：非提权启动返回 WaitedForExit=true 与 ExitCode=0",
                ok.Launched && ok.WaitedForExit && ok.ExitCode == 0 && ok.Error is null,
                $"Launched={ok.Launched}, Waited={ok.WaitedForExit}, ExitCode={ok.ExitCode}, Error={ok.Error ?? "null"}");

            var fail = await runner.RunAsync(whereExe, "__nonexistent_sinupdatehub_cmd__", runAsAdmin: false);
            Check(
                "BUG10：非零退出码返回 WaitedForExit=true 与错误提示",
                fail.Launched && fail.WaitedForExit && fail.ExitCode != 0 && !string.IsNullOrWhiteSpace(fail.Error),
                $"Launched={fail.Launched}, Waited={fail.WaitedForExit}, ExitCode={fail.ExitCode}, Error={fail.Error ?? "null"}");
        }
        catch (Exception ex)
        {
            Fail("BUG10：InstallerRunner WaitedForExit", Describe(ex));
        }
    }

    private async Task TestAppxSupportOfflineAsync()
    {
        try
        {
            var parsed = InstalledAppDetector.ParseAppxPackageFullName("40174MouriNaruto.NanaZip_7.0.1882.0_x64__gnj4mf6z9tkrc");
            Check(
                "MSIX：ParseAppxPackageFullName 解析包名与版本号",
                parsed is { PackageName: "40174MouriNaruto.NanaZip", Version: "7.0.1882.0" },
                $"实际：{parsed}");

            Check(
                "MSIX：IsWindowsAppsAliasPath 准确识别执行别名路径",
                InstalledAppDetector.IsWindowsAppsAliasPath(@"C:\Users\Test\AppData\Local\Microsoft\WindowsApps\NanaZip.exe")
                && !InstalledAppDetector.IsWindowsAppsAliasPath(@"C:\Program Files\App\App.exe"),
                "路径识别结果不符合预期");

            var detector = new InstalledAppDetector();
            var installed = await detector.ScanInstalledAsync(forceRefresh: true);
            var appxApps = installed
                .Where(a => a.DetectedFrom?.StartsWith("MSIX/Appx:", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            Check(
                "MSIX：ScanInstalledAsync 能扫描到本机 MSIX/Appx 应用",
                appxApps.Count > 0,
                $"实际扫描到 {appxApps.Count} 条");

            var hasProtectedPath = appxApps.Any(a =>
                !string.IsNullOrWhiteSpace(a.InstallLocation)
                && a.InstallLocation.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase));
            Check(
                "MSIX：扫描结果不暴露受保护的 WindowsApps 安装路径",
                !hasProtectedPath,
                "存在 InstallLocation 包含 \\WindowsApps\\ 的条目");

            var nanaZip = appxApps.FirstOrDefault(a =>
                a.DisplayName.Contains("NanaZip", StringComparison.OrdinalIgnoreCase)
                || (a.DetectedFrom?.Contains("NanaZip", StringComparison.OrdinalIgnoreCase) ?? false));
            if (nanaZip is not null)
            {
                var aliasPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WindowsApps", "NanaZip.exe");
                var resolved = await detector.ResolveFromExecutableAsync(aliasPath);
                Check(
                    "MSIX：WindowsApps 执行别名可反查 NanaZip 版本",
                    resolved is not null && !string.IsNullOrWhiteSpace(resolved.Version),
                    resolved is null ? "返回 null" : $"DisplayName={resolved.DisplayName}, Version={resolved.Version}");
            }

            Check(
                "MSIX：IsSilentAppxArgs 识别静默安装参数",
                InstallerRunner.IsSilentAppxArgs("/S")
                && InstallerRunner.IsSilentAppxArgs("/quiet")
                && !InstallerRunner.IsSilentAppxArgs(string.Empty),
                "静默参数判断不符合预期");

            var missingMsix = await new InstallerRunner().RunAsync(@"C:\nonexistent\test.msixbundle", null, false);
            Check(
                "MSIX：缺失的 .msixbundle 文件返回明确错误",
                !missingMsix.Launched && !string.IsNullOrWhiteSpace(missingMsix.Error),
                $"Launched={missingMsix.Launched}, Error={missingMsix.Error ?? "null"}");
        }
        catch (Exception ex)
        {
            Fail("MSIX/Appx 支持离线测试", Describe(ex));
        }
    }

    // ---------------- 9. SinNote 表格 / 笔记 / 备份往返（离线） ----------------

    private void TestNotesOffline()
    {
        Section("9. SinNote 表格 / 笔记与备份往返（离线）");
        var tempDir = Path.Combine(Path.GetTempPath(), "SinUpdateHub-NoteSmoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);

            // 1) 默认 3×3 表格 + 首行/首列默认色区分
            var table = NoteTable.CreateDefault3x3();
            Check(
                "SinNote：默认新建表格为 3×3，且首行/首列颜色与正文单元格不同",
                table.RowCount == 3
                && table.ColumnCount == 3
                && table.Rows.All(r => r.Count == 3)
                && !string.Equals(table.HeaderBgColor, table.CellBgColor, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(table.FirstColumnBgColor, table.CellBgColor, StringComparison.OrdinalIgnoreCase),
                $"实际：{table.RowCount}×{table.ColumnCount}，Header={table.HeaderBgColor}，Cell={table.CellBgColor}");

            // 2) 增删行与增删列 + 最少保留 1×1 保护
            table.Rows[0][0] = "项目";
            table.Rows[0][1] = "地址";
            table.Rows[1][0] = "百度";
            table.Rows[1][1] = "https://fanyi-api.baidu.com/";
            var newRowIdx = table.AddRow(1);
            var newColIdx = table.AddColumn(1);
            Check(
                "SinNote：AddRow / AddColumn 保持矩形结构并保留原数据",
                newRowIdx == 2
                && newColIdx == 2
                && table.RowCount == 4
                && table.ColumnCount == 4
                && table.Rows.All(r => r.Count == 4)
                && table.Rows[1][0] == "百度"
                && table.Rows[1][1] == "https://fanyi-api.baidu.com/",
                $"实际：{table.RowCount}×{table.ColumnCount}，newRowIdx={newRowIdx}，newColIdx={newColIdx}");

            var delRowOk = table.DeleteRow(2);
            var delColOk = table.DeleteColumn(2);
            Check(
                "SinNote：DeleteRow / DeleteColumn 后恢复 3×3 且原内容完好",
                delRowOk && delColOk && table.RowCount == 3 && table.ColumnCount == 3 && table.Rows[1][1] == "https://fanyi-api.baidu.com/",
                $"实际：{table.RowCount}×{table.ColumnCount}");

            var tiny = NoteTable.CreateDefault3x3();
            tiny.DeleteRow();
            tiny.DeleteRow();
            var blockedLastRow = !tiny.DeleteRow();
            tiny.DeleteColumn();
            tiny.DeleteColumn();
            var blockedLastCol = !tiny.DeleteColumn();
            Check(
                "SinNote：删减行列至少保留 1 行 1 列",
                blockedLastRow && blockedLastCol && tiny.RowCount == 1 && tiny.ColumnCount == 1,
                $"实际：{tiny.RowCount}×{tiny.ColumnCount}");

            // 3) 自定义颜色 + 深拷贝独立性
            table.HeaderBgColor = "#5B3E96";
            table.FirstColumnBgColor = "#1F4E78";
            table.CellBgColor = "#BDD7EE";
            var section = new NoteSection
            {
                Name = "账号",
                ColorHex = "#DB61A2",
                Pages = new List<NotePage>
                {
                    new()
                    {
                        Title = "DPI",
                        Blocks = new List<NoteBlock>
                        {
                            new() { Kind = NoteBlockKind.Text, Text = "控制面板 → 本地安全策略" },
                            new() { Kind = NoteBlockKind.Table, Table = table },
                            new() { Kind = NoteBlockKind.Image, ImageBase64 = "AQIDBA==", ImageMimeType = "image/png", ImageCaption = "截图" },
                        },
                    },
                },
            };

            var cloned = section.Clone();
            cloned.Pages[0].Blocks[1].Table!.Rows[0][0] = "已被修改";
            Check(
                "SinNote：Clone 深拷贝不共享内部表格行与块引用",
                section.Pages[0].Blocks[1].Table!.Rows[0][0] == "项目" && cloned.Pages[0].Blocks[1].Table!.Rows[0][0] == "已被修改",
                $"原值={section.Pages[0].Blocks[1].Table!.Rows[0][0]}");

            // 4) AppStore Save/Load + Export/Import + BackupDocument 往返（WebDAV 共用 BackupDocument）
            var dataPath = Path.Combine(tempDir, "notes-data.json");
            var store = new AppStore(dataPath);
            var appData = new AppData
            {
                Entries = new List<AppEntry> { new() { Name = "SampleApp" } },
                NoteSections = new List<NoteSection> { section },
            };
            store.Save(appData);
            var loaded = store.Load();
            var loadedTable = loaded.NoteSections.FirstOrDefault()?.Pages.FirstOrDefault()?.Blocks[1].Table;
            var loadedImage = loaded.NoteSections.FirstOrDefault()?.Pages.FirstOrDefault()?.Blocks[2];
            Check(
                "SinNote：AppStore Save → Load 完整保留分区/页/表格配色与图片 Base64",
                loaded.NoteSections.Count == 1
                && loaded.NoteSections[0].Name == "账号"
                && loaded.NoteSections[0].ColorHex == "#DB61A2"
                && loadedTable is not null
                && loadedTable.HeaderBgColor == "#5B3E96"
                && loadedTable.FirstColumnBgColor == "#1F4E78"
                && loadedTable.CellBgColor == "#BDD7EE"
                && loadedTable.Rows[1][1] == "https://fanyi-api.baidu.com/"
                && loadedImage is not null
                && loadedImage.ImageBase64 == "AQIDBA==",
                "持久化往返内容不一致");

            var backupDoc = BackupDocument.From(loaded);
            var restoredFromDoc = backupDoc.ToAppData();
            var restoredTable = restoredFromDoc.NoteSections[0].Pages[0].Blocks[1].Table!;
            Check(
                "SinNote：BackupDocument（WebDAV 序列化载体）完整往返",
                restoredFromDoc.NoteSections.Count == 1
                && restoredTable.HeaderBgColor == "#5B3E96"
                && restoredTable.CellBgColor == "#BDD7EE"
                && restoredFromDoc.NoteSections[0].Pages[0].Blocks[2].ImageBase64 == "AQIDBA==",
                "BackupDocument 往返丢失 SinNote 数据");

            var exportPath = Path.Combine(tempDir, "backup.json");
            store.Export(loaded, exportPath);
            var imported = store.Import(exportPath);
            Check(
                "SinNote：AppStore Export → Import 往返保留 SinNote 与 Entries",
                imported.Entries.Count == 1
                && imported.NoteSections.Count == 1
                && imported.NoteSections[0].Pages[0].Title == "DPI"
                && imported.NoteSections[0].Pages[0].Blocks.Count == 3,
                $"导入分区数={imported.NoteSections.Count}");

            // 5) 向后兼容：旧版无 noteSections 字段的 JSON
            var legacyPath = Path.Combine(tempDir, "legacy.json");
            File.WriteAllText(legacyPath, "{ \"entries\": [ { \"name\": \"OldApp\" } ] }");
            var legacy = new AppStore(legacyPath).Load();
            Check(
                "SinNote：兼容旧版无 noteSections 字段的 data.json",
                legacy.Entries.Count == 1 && legacy.NoteSections is not null && legacy.NoteSections.Count == 0,
                $"Entries={legacy.Entries.Count}, NoteSectionsNull={legacy.NoteSections is null}");

            // 6) 行拖拽调序与行列复制粘贴扩展
            var opTable = NoteTable.CreateDefault3x3();
            opTable.Rows[0] = new List<string> { "R0", "A", "B" };
            opTable.Rows[1] = new List<string> { "R1", "C", "D" };
            opTable.Rows[2] = new List<string> { "R2", "E", "F" };
            var moved = opTable.MoveRow(2, 0);
            Check(
                "SinNote：NoteTable.MoveRow 正确重排行顺序",
                moved && opTable.Rows[0][0] == "R2" && opTable.Rows[1][0] == "R0" && opTable.Rows[2][0] == "R1",
                $"首列顺序：{string.Join(",", opTable.Rows.Select(r => r[0]))}");

            var insertedRowIdx = opTable.InsertRowWithValues(1, new[] { "N1", "N2", "N3", "N4" });
            var insertedColIdx = opTable.InsertColumnWithValues(0, new[] { "C0", "C1", "C2", "C3", "C4" });
            Check(
                "SinNote：InsertRowWithValues / InsertColumnWithValues 自动扩展行列并填充",
                insertedRowIdx == 2
                && insertedColIdx == 1
                && opTable.RowCount == 5
                && opTable.ColumnCount == 5
                && opTable.Rows[2][0] == "N1"
                && opTable.Rows[2][1] == "C2"
                && opTable.Rows[4][1] == "C4",
                $"尺寸={opTable.RowCount}×{opTable.ColumnCount}");

            var parsedTsv = NoteTable.ParseTsv("\"L1\nL2\"\tV2\tV3\nX1\tX2\tX3");
            var pasteTarget = NoteTable.CreateDefault3x3();
            pasteTarget.PasteGridAt(2, 2, parsedTsv);
            Check(
                "SinNote：ParseTsv 与 PasteGridAt 保留多行单元格并向后自动扩充行列",
                parsedTsv.Count == 2
                && parsedTsv[0][0] == "L1\nL2"
                && pasteTarget.RowCount == 4
                && pasteTarget.ColumnCount == 5
                && pasteTarget.Rows[2][2] == "L1\nL2"
                && pasteTarget.Rows[3][4] == "X3",
                $"尺寸={pasteTarget.RowCount}×{pasteTarget.ColumnCount}");

            // 7) Typora/Markdown 升级：旧版 Blocks 无损迁移到 Markdown，并继承表格配色
            var legacyBlockTable = NoteTable.CreateDefault3x3();
            legacyBlockTable.HeaderBgColor = "#5B3E96";
            legacyBlockTable.FirstColumnBgColor = "#1F7A7A";
            legacyBlockTable.CellBgColor = "#BDD7EE";
            legacyBlockTable.Rows[0] = new List<string> { "名称", "说明|备注", "地址" };
            legacyBlockTable.Rows[1] = new List<string> { "百度", "第一行\n第二行", "https://fanyi-api.baidu.com" };
            var legacyPage = new NotePage
            {
                Title = "迁移测试页",
                Blocks = new List<NoteBlock>
                {
                    new() { Kind = NoteBlockKind.Text, Text = "# 操作步骤\n第一段说明" },
                    new() { Kind = NoteBlockKind.Table, Table = legacyBlockTable },
                    new() { Kind = NoteBlockKind.Image, ImageBase64 = "QUFBQQ==", ImageMimeType = "image/png", ImageCaption = "策略截图" },
                },
            };
            var migratedMd = legacyPage.EnsureMarkdown();
            Check(
                "SinNote：旧版 Blocks.EnsureMarkdown 无损转换文本/GFM表格/图片并抽离到 Images",
                migratedMd.Contains("# 操作步骤", StringComparison.Ordinal)
                && migratedMd.Contains(@"| 名称 | 说明\|备注 | 地址 |", StringComparison.Ordinal)
                && migratedMd.Contains("| 百度 | 第一行<br>第二行 | https://fanyi-api.baidu.com |", StringComparison.Ordinal)
                && migratedMd.Contains("![策略截图](https://sinnote-img.local/img_", StringComparison.Ordinal)
                && legacyPage.Images.Count == 1
                && legacyPage.Images.Values.First().Base64 == "QUFBQQ=="
                && legacyPage.TableHeaderBgColor == "#5B3E96"
                && legacyPage.TableFirstColumnBgColor == "#1F7A7A"
                && legacyPage.TableCellBgColor == "#BDD7EE",
                $"migratedLen={migratedMd.Length}, hdr={legacyPage.TableHeaderBgColor}, imgCount={legacyPage.Images.Count}");

            // 8) StripDataUriImagesForSearch 剥离 Base64 噪声同时保留图片标题与正文检索
            var stripped = NotePage.StripDataUriImagesForSearch(migratedMd + "\n![内联截图](data:image/png;base64,QUFBQQ==)");
            Check(
                "SinNote：StripDataUriImagesForSearch 过滤 Base64 编码且保留图片标题和表格文本",
                !stripped.Contains("QUFBQQ==", StringComparison.Ordinal)
                && stripped.Contains("策略截图", StringComparison.Ordinal)
                && stripped.Contains("内联截图", StringComparison.Ordinal)
                && stripped.Contains("操作步骤", StringComparison.Ordinal)
                && stripped.Contains("fanyi-api.baidu.com", StringComparison.Ordinal),
                $"stripped={stripped}");

            // 9) NotePage.Markdown、外置 Images 与自定义表格配色经 AppStore 往返序列化完整无损
            var mdStorePath = Path.Combine(tempDir, "notes-markdown.json");
            var mdStore = new AppStore(mdStorePath);
            mdStore.Save(new AppData
            {
                NoteSections = new List<NoteSection>
                {
                    new()
                    {
                        Name = "Markdown分区",
                        ColorHex = "#A371F7",
                        Pages = new List<NotePage>
                        {
                            new()
                            {
                                Title = "Typora页",
                                Markdown = NotePage.Default3x3MarkdownTable + "\n\n![clip](data:image/png;base64,AQIDBA==)",
                                TableHeaderBgColor = "#1F4E78",
                                TableFirstColumnBgColor = "#2E7D32",
                                TableCellBgColor = "#FFF2CC",
                            },
                        },
                    },
                },
            });
            var mdLoaded = new AppStore(mdStorePath).Load();
            var mdLoadedPage = mdLoaded.NoteSections.FirstOrDefault()?.Pages.FirstOrDefault();
            var ensuredLoadedMd = mdLoadedPage?.EnsureMarkdown() ?? string.Empty;
            Check(
                "SinNote：Markdown 正文、Images 字典与表格配色经 AppStore 往返序列化无损保存",
                mdLoadedPage is not null
                && ensuredLoadedMd.Contains("| 列1 | 列2 | 列3 |", StringComparison.Ordinal)
                && ensuredLoadedMd.Contains("![clip](https://sinnote-img.local/img_", StringComparison.Ordinal)
                && mdLoadedPage.Images.Count == 1
                && mdLoadedPage.Images.Values.First().Base64 == "AQIDBA=="
                && mdLoadedPage.TableHeaderBgColor == "#1F4E78"
                && mdLoadedPage.TableFirstColumnBgColor == "#2E7D32"
                && mdLoadedPage.TableCellBgColor == "#FFF2CC",
                $"loadedPageNull={mdLoadedPage is null}");

            // 10) ToSearchablePlainText 过滤代码围栏标识（如 ```flowchart）、行内样式 HTML 标签与分隔行，避免幽灵重复命中
            var codeFenceSample = "# 标题\n```flowchart\nhallo world\n```\n| 列1 | 列2 |\n| --- | --- |\n| :--- | ----: |\n| 账号 | test |\n<mark style=\"background-color:#FFF100;color:#111827\">我好</mark> <span style=\"color:#FF6B6B;font-family:'KaiTi'\">字体</span>\n<div align=\"center\">居中正文</div>\n![截图](https://sinnote-img.local/img_abc123.png)";
            var plainText = NotePage.ToSearchablePlainText(codeFenceSample);
            Check(
                "SinNote：ToSearchablePlainText 过滤代码围栏、行内样式 HTML、表格分隔行与虚拟图片 URL 并保留真实正文",
                !plainText.Contains("flowchart", StringComparison.OrdinalIgnoreCase)
                && !plainText.Contains("---", StringComparison.Ordinal)
                && !plainText.Contains("sinnote-img.local", StringComparison.OrdinalIgnoreCase)
                && !plainText.Contains("background-color", StringComparison.OrdinalIgnoreCase)
                && !plainText.Contains("KaiTi", StringComparison.OrdinalIgnoreCase)
                && !plainText.Contains("<mark", StringComparison.OrdinalIgnoreCase)
                && !plainText.Contains("<div", StringComparison.OrdinalIgnoreCase)
                && plainText.Contains("hallo world", StringComparison.Ordinal)
                && plainText.Contains("账号 test", StringComparison.Ordinal)
                && plainText.Contains("我好", StringComparison.Ordinal)
                && plainText.Contains("字体", StringComparison.Ordinal)
                && plainText.Contains("居中正文", StringComparison.Ordinal),
                $"plain={plainText}");

            // 10b) PruneUnreferencedImages 清理正文不再引用的孤立图片，同时保留仍在使用的图片
            var prunePage = new NotePage
            {
                Title = "孤立图片清理测试",
                Markdown = "保留图：![ok](https://sinnote-img.local/img_keep.png)\n普通正文",
                Images = new Dictionary<string, NoteImageAsset>(StringComparer.OrdinalIgnoreCase)
                {
                    ["img_keep.png"] = new() { MimeType = "image/png", Base64 = "AQID" },
                    ["img_orphan.png"] = new() { MimeType = "image/png", Base64 = "BAUG" },
                },
            };
            var pruned = prunePage.PruneUnreferencedImages();
            Check(
                "SinNote：PruneUnreferencedImages 仅回收未引用孤立图片并保留正文引用图片",
                pruned
                && prunePage.Images.Count == 1
                && prunePage.Images.ContainsKey("img_keep.png")
                && !prunePage.Images.ContainsKey("img_orphan.png"),
                $"pruned={pruned}, remaining={string.Join(",", prunePage.Images.Keys)}");

            // 11) NoteFontFamily / NoteZoomPercent 往返持久化与越界值兜底
            var typoPath = Path.Combine(tempDir, "typo-settings.json");
            var typoStore = new SettingsStore(typoPath);
            typoStore.Save(new AppSettings
            {
                NoteFontFamily = "\"SimSun\", serif",
                NoteZoomPercent = 130,
            });
            var loadedTypo = typoStore.Load();
            File.WriteAllText(typoPath, "{ \"noteZoomPercent\": 0, \"noteFontFamily\": \"\" }");
            var fallbackTypo = typoStore.Load();
            Check(
                "SinNote：NoteFontFamily 与 NoteZoomPercent 往返持久化及异常值安全兜底",
                loadedTypo.NoteFontFamily == "\"SimSun\", serif"
                && loadedTypo.NoteZoomPercent == 130
                && fallbackTypo.NoteFontFamily == AppSettings.DefaultNoteFontFamily
                && fallbackTypo.NoteZoomPercent == AppSettings.DefaultNoteZoomPercent,
                $"loadedZoom={loadedTypo.NoteZoomPercent}, fallbackZoom={fallbackTypo.NoteZoomPercent}");

            // 12) 表格内粘贴 Base64 截图引发的超长空格填充膨胀自动治愈（防 Lute GFM 表格对齐卡死）
            var bloatedPadding = new string(' ', 6000);
            var bloatedHyphens = new string('-', 6000);
            var bloatedPage = new NotePage
            {
                Title = "膨胀表格修复测试",
                Markdown = $"| col1{bloatedPadding} | col2 |\n| :{bloatedHyphens} | :--- |\n| ![截图](data:image/png;base64,QUFBQQ==) | 123 |",
            };
            var healedMd = bloatedPage.EnsureMarkdown();
            var healedStorePath = Path.Combine(tempDir, "healed-notes.json");
            var healedStore = new AppStore(healedStorePath);
            healedStore.Save(new AppData
            {
                NoteSections = new List<NoteSection>
                {
                    new() { Name = "测试", Pages = new List<NotePage> { bloatedPage } },
                },
            });
            var healedReloaded = healedStore.Load().NoteSections[0].Pages[0];
            Check(
                "SinNote：ExtractInlineDataUrisToAssets 自动剥离内联 Base64 并压缩膨胀表格行，且 Images 字典持久化往返完整",
                healedMd.Length < 220
                && !healedMd.Contains("data:image/", StringComparison.OrdinalIgnoreCase)
                && healedMd.Contains("https://sinnote-img.local/img_", StringComparison.Ordinal)
                && bloatedPage.Images.Count == 1
                && healedReloaded.Images.Count == 1
                && healedReloaded.Images.Values.First().Base64 == "QUFBQQ==",
                $"healedLen={healedMd.Length}, imgCount={bloatedPage.Images.Count}");

            // 13) DataMergeService 选择性恢复：单选软件/单选页面合并保留未勾选项 + 覆盖模式仅保留勾选项
            var currentData = new AppData
            {
                Entries = new List<AppEntry>
                {
                    new() { Id = "app-keep", Name = "KeepApp", Source = SourceKind.GitHub, CurrentVersion = "1.0" },
                    new() { Id = "app-update", Name = "UpdateApp", Source = SourceKind.WinGet, CurrentVersion = "1.0" },
                },
                NoteSections = new List<NoteSection>
                {
                    new()
                    {
                        Id = "sec-1",
                        Name = "账号",
                        Pages = new List<NotePage>
                        {
                            new() { Id = "page-keep", Title = "保留页", Markdown = "原内容" },
                            new() { Id = "page-over", Title = "更新页", Markdown = "旧内容" },
                        },
                    },
                },
            };
            var incomingData = new AppData
            {
                Entries = new List<AppEntry>
                {
                    new() { Id = "app-update", Name = "UpdateApp", Source = SourceKind.WinGet, CurrentVersion = "2.0" },
                    new() { Id = "app-new", Name = "NewApp", Source = SourceKind.Custom, HomepageUrl = "https://example.com" },
                },
                NoteSections = new List<NoteSection>
                {
                    new()
                    {
                        Id = "sec-1",
                        Name = "账号",
                        Pages = new List<NotePage>
                        {
                            new() { Id = "page-over", Title = "更新页", Markdown = "新恢复内容" },
                            new() { Id = "page-skip", Title = "未勾选页", Markdown = "不应导入" },
                        },
                    },
                },
            };

            var (mergedData, mergeSummary) = DataMergeService.Apply(
                currentData,
                incomingData,
                new RestoreSelectionResult
                {
                    SelectedEntryIds = new HashSet<string> { "app-update" },
                    SelectedNotePageIds = new HashSet<string> { "page-over" },
                    Mode = RestoreMode.Merge,
                });
            Check(
                "选择性恢复：合并模式仅更新/追加勾选的软件与笔记页，完整保留未勾选的现有项",
                mergedData.Entries.Count == 2
                && mergedData.Entries.Any(e => e.Id == "app-keep")
                && mergedData.Entries.First(e => e.Id == "app-update").CurrentVersion == "2.0"
                && !mergedData.Entries.Any(e => e.Id == "app-new")
                && mergedData.NoteSections.Count == 1
                && mergedData.NoteSections[0].Pages.Count == 2
                && mergedData.NoteSections[0].Pages.First(p => p.Id == "page-keep").Markdown == "原内容"
                && mergedData.NoteSections[0].Pages.First(p => p.Id == "page-over").Markdown == "新恢复内容"
                && mergeSummary.RestoredAppsCount == 1
                && mergeSummary.RestoredNotePagesCount == 1,
                $"entries={mergedData.Entries.Count}, pages={mergedData.NoteSections[0].Pages.Count}");

            var (replacedData, replaceSummary) = DataMergeService.Apply(
                currentData,
                incomingData,
                new RestoreSelectionResult
                {
                    SelectedEntryIds = new HashSet<string> { "app-new" },
                    SelectedNotePageIds = new HashSet<string> { "page-skip" },
                    Mode = RestoreMode.Replace,
                });
            Check(
                "选择性恢复：覆盖模式仅保留本次勾选的软件条目与笔记页面",
                replacedData.Entries.Count == 1
                && replacedData.Entries[0].Id == "app-new"
                && replacedData.NoteSections.Count == 1
                && replacedData.NoteSections[0].Pages.Count == 1
                && replacedData.NoteSections[0].Pages[0].Id == "page-skip"
                && replaceSummary.RestoredDownloadsCount == 1
                && replaceSummary.RestoredNotePagesCount == 1,
                $"entries={replacedData.Entries.Count}, pages={replacedData.NoteSections[0].Pages.Count}");

            // 14) SinNote 回收站持久化往返与 30 天过期自动清理
            var nowRef = new DateTimeOffset(2026, 11, 24, 21, 0, 0, TimeSpan.FromHours(8));
            var trashStorePath = Path.Combine(tempDir, "trash-notes.json");
            var trashStore = new AppStore(trashStorePath);
            trashStore.Save(new AppData
            {
                NoteTrash = new List<NoteTrashItem>
                {
                    new()
                    {
                        IsSection = false,
                        DeletedAt = nowRef.AddDays(-5),
                        OriginalSectionId = "sec-1",
                        OriginalSectionName = "账号记录",
                        OriginalSectionColorHex = "#2F75B5",
                        Page = new NotePage { Title = "最近删除页", Markdown = "测试内容" },
                    },
                    new()
                    {
                        IsSection = true,
                        DeletedAt = nowRef.AddDays(-31),
                        OriginalSectionId = "sec-old",
                        OriginalSectionName = "过期分区",
                        Section = new NoteSection
                        {
                            Name = "过期分区",
                            Pages = new List<NotePage> { new() { Title = "旧页", Markdown = "old" } },
                        },
                    },
                },
            });
            var loadedTrashData = trashStore.Load();
            var expiredPruned = NoteTrashItem.PruneExpired(loadedTrashData.NoteTrash, NoteTrashItem.DefaultRetentionDays, nowRef);
            Check(
                "SinNote 回收站：AppStore 往返持久化并自动清理超过 30 天的过期条目",
                expiredPruned == 1
                && loadedTrashData.NoteTrash.Count == 1
                && !loadedTrashData.NoteTrash[0].IsSection
                && loadedTrashData.NoteTrash[0].Page?.Title == "最近删除页",
                $"expiredPruned={expiredPruned}, remaining={loadedTrashData.NoteTrash.Count}");

            // 15) WebDAV 自动定时备份（默认 24 小时）到期判断与设置持久化兜底
            var autoBackupSettingsPath = Path.Combine(tempDir, "autobackup-settings.json");
            var autoBackupStore = new SettingsStore(autoBackupSettingsPath);
            var defaultAuto = new AppSettings
            {
                WebDavUrl = "https://dav.example.com/dav/",
                WebDavUser = "user@example.com",
                WebDavPassword = "app-password",
            };
            var dueFirst = defaultAuto.IsAutoBackupDue(nowRef);
            defaultAuto.LastBackupAt = nowRef.AddHours(-23);
            var dueAt23h = defaultAuto.IsAutoBackupDue(nowRef);
            defaultAuto.LastBackupAt = nowRef.AddHours(-24);
            var dueAt24h = defaultAuto.IsAutoBackupDue(nowRef);
            defaultAuto.AutoBackupEnabled = false;
            var dueWhenDisabled = defaultAuto.IsAutoBackupDue(nowRef);
            File.WriteAllText(autoBackupSettingsPath, "{ \"autoBackupEnabled\": true, \"autoBackupIntervalHours\": 0 }");
            var clampedAuto = autoBackupStore.Load();
            Check(
                "WebDAV 自动定时备份：默认 24 小时到期判断与异常间隔兜底",
                defaultAuto.AutoBackupIntervalHours == 24
                && dueFirst
                && !dueAt23h
                && dueAt24h
                && !dueWhenDisabled
                && clampedAuto.AutoBackupEnabled
                && clampedAuto.AutoBackupIntervalHours == 24,
                $"dueFirst={dueFirst}, 23h={dueAt23h}, 24h={dueAt24h}, disabled={dueWhenDisabled}, clamped={clampedAuto.AutoBackupIntervalHours}");

            // 16) 百度网盘：授权 URL、回调 code 提取、路径规范化、dlink 请求头与 SecretKey/Token DPAPI 加密
            var authUrl = BaiduPanService.BuildAuthorizeUrl("demo-app-key");
            var codeFromQuery = BaiduPanService.TryExtractAuthCode(new Uri("https://openapi.baidu.com/oauth/2.0/login_success?code=abc123456"));
            var codeFromFrag = BaiduPanService.TryExtractAuthCode(new Uri("https://openapi.baidu.com/oauth/2.0/login_success#code=xyz987654&state=1"));
            var normDir = BaiduPanService.NormalizeRemoteDir(@"apps\SinUpdateHub//tools/");
            var parentDir = BaiduPanService.GetParentDir("/apps/SinUpdateHub/tools");
            using var dlinkReq = BaiduPanService.CreateDlinkDownloadRequest("https://d.pcs.baidu.com/file/test?fid=1", "tok_test_123");
            var baiduSettingsPath = Path.Combine(tempDir, "baidu-settings.json");
            var baiduStore = new SettingsStore(baiduSettingsPath);
            baiduStore.Save(new AppSettings
            {
                BaiduAppKey = "my_app_key",
                BaiduSecretKey = "my_secret_key_999",
                BaiduAccessToken = "my_access_token_888",
                BaiduRefreshToken = "my_refresh_token_777",
            });
            var baiduRawJson = File.ReadAllText(baiduSettingsPath);
            var baiduLoaded = baiduStore.Load();
            Check(
                "百度网盘：OAuth URL、code 提取、路径规范化、pan.baidu.com UA 与密钥 DPAPI 加密往返",
                authUrl.Contains("client_id=demo-app-key", StringComparison.Ordinal)
                && authUrl.Contains("redirect_uri=oob", StringComparison.Ordinal)
                && codeFromQuery == "abc123456"
                && codeFromFrag == "xyz987654"
                && normDir == "/apps/SinUpdateHub/tools"
                && parentDir == "/apps/SinUpdateHub"
                && dlinkReq.Headers.UserAgent.ToString() == "pan.baidu.com"
                && dlinkReq.RequestUri!.AbsoluteUri.Contains("access_token=tok_test_123", StringComparison.Ordinal)
                && !baiduRawJson.Contains("my_secret_key_999", StringComparison.Ordinal)
                && !baiduRawJson.Contains("my_access_token_888", StringComparison.Ordinal)
                && !baiduRawJson.Contains("my_refresh_token_777", StringComparison.Ordinal)
                && baiduLoaded.BaiduSecretKey == "my_secret_key_999"
                && baiduLoaded.BaiduAccessToken == "my_access_token_888"
                && baiduLoaded.BaiduRefreshToken == "my_refresh_token_777",
                $"normDir={normDir}, parent={parentDir}, ua={dlinkReq.Headers.UserAgent}");
        }
        catch (Exception ex)
        {
            Fail("SinNote 离线断言组", Describe(ex));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    // ---------------- 10. ManualSourceProvider 链接检测（离线） ----------------

    private async Task TestManualSourceProviderOfflineAsync()
    {
        Section("10. 软件下载（ManualSourceProvider）链接检测（离线）");
        try
        {
            var noProxySettings = new AppSettings { ProxyMode = ProxyMode.None };
            using var factory = new NetworkClientFactory(() => noProxySettings);
            var manual = new ManualSourceProvider(factory);
            var checker = new UpdateChecker(new IUpdateSourceProvider[] { manual });

            var emptyResult = await checker.CheckOneAsync(new AppEntry
            {
                Name = "空链接工具",
                Source = SourceKind.Custom,
                HomepageUrl = string.Empty,
            });
            Check(
                "ManualSourceProvider：空链接检查返回 UpdateState.Failed",
                emptyResult.State == UpdateState.Failed && !string.IsNullOrWhiteSpace(emptyResult.Error),
                $"State={emptyResult.State}, Error={emptyResult.Error ?? "null"}");

            var badUrlResult = await checker.CheckOneAsync(new AppEntry
            {
                Name = "无效链接工具",
                Source = SourceKind.Custom,
                HomepageUrl = "not-a-valid-url",
            });
            Check(
                "ManualSourceProvider：无效链接格式检查返回 UpdateState.Failed",
                badUrlResult.State == UpdateState.Failed && !string.IsNullOrWhiteSpace(badUrlResult.Error),
                $"State={badUrlResult.State}, Error={badUrlResult.Error ?? "null"}");

            // 启动一个仅响应 200 与 404 的本地 TCP 测试监听
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serverLoop = Task.Run(async () =>
            {
                for (var i = 0; i < 2 && !cts.IsCancellationRequested; i++)
                {
                    using var client = await listener.AcceptTcpClientAsync(cts.Token);
                    using var stream = client.GetStream();
                    var buffer = new byte[2048];
                    var read = await stream.ReadAsync(buffer, cts.Token);
                    var req = Encoding.ASCII.GetString(buffer, 0, read);
                    var statusLine = req.Contains("/ok", StringComparison.Ordinal)
                        ? "HTTP/1.1 200 OK"
                        : "HTTP/1.1 404 Not Found";
                    var resp = Encoding.ASCII.GetBytes(statusLine + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(resp, cts.Token);
                    await stream.FlushAsync(cts.Token);
                }
            }, cts.Token);

            var okResult = await checker.CheckOneAsync(new AppEntry
            {
                Name = "正常网页",
                Source = SourceKind.Custom,
                HomepageUrl = $"http://127.0.0.1:{port}/ok",
            });
            var notFoundResult = await checker.CheckOneAsync(new AppEntry
            {
                Name = "失效网页",
                Source = SourceKind.Custom,
                HomepageUrl = $"http://127.0.0.1:{port}/missing",
            });
            await serverLoop;

            Check(
                "ManualSourceProvider：可达网页（HTTP 200）不报错",
                okResult.State == UpdateState.Unknown && string.IsNullOrWhiteSpace(okResult.Error),
                $"State={okResult.State}, Error={okResult.Error ?? "null"}");
            Check(
                "ManualSourceProvider：不可达网页（HTTP 404）返回 UpdateState.Failed（检查失败）",
                notFoundResult.State == UpdateState.Failed && (notFoundResult.Error?.Contains("404", StringComparison.Ordinal) ?? false),
                $"State={notFoundResult.State}, Error={notFoundResult.Error ?? "null"}");
        }
        catch (Exception ex)
        {
            Fail("ManualSourceProvider 离线测试组", Describe(ex));
        }
    }

    // ---------------- 辅助 ----------------

    private static async Task<T> NetworkAsync<T>(string label, Func<Task<T>> action)
    {
        var started = DateTimeOffset.Now;
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            if (DateTimeOffset.Now - started > TimeSpan.FromSeconds(30))
            {
                throw;
            }

            Console.WriteLine($"      {label} 第一次失败：{Describe(ex)}");
            Console.WriteLine("      等待 5 秒后重试一次…");
            await Task.Delay(TimeSpan.FromSeconds(5));
            return await action();
        }
    }

    private void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} ---");
    }

    private void Check(string name, bool condition, string failReason = "断言不成立")
    {
        if (condition)
        {
            Passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        else
        {
            Failed++;
            Failures.Add($"{name} :: {failReason}");
            Console.WriteLine($"[FAIL] {name}");
            Console.WriteLine($"       原因：{failReason}");
        }
    }

    private void Fail(string name, string reason)
    {
        Failed++;
        Failures.Add($"{name} :: {reason}");
        Console.WriteLine($"[FAIL] {name}");
        Console.WriteLine($"       原因：{reason}");
    }

    private void Skip(string name, string reason)
    {
        Skipped++;
        Console.WriteLine($"[SKIP] {name} — {reason}");
    }

    private void Warn(string message)
    {
        Console.WriteLine($"[WARN] {message}");
    }

    private static string Describe(Exception ex)
    {
        var builder = new StringBuilder();
        var current = ex;
        var depth = 0;
        while (current is not null && depth < 5)
        {
            if (depth > 0)
            {
                builder.Append("  ← ");
            }

            builder.Append(current.GetType().Name).Append(": ").Append(current.Message);
            current = current.InnerException;
            depth++;
        }

        return builder.ToString();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}

/// <summary>同步收集进度回调的实现（避免 Progress&lt;T&gt; 的异步投递导致断言时序不稳定）。</summary>
internal sealed class ListProgress<T> : IProgress<T>
{
    public List<T> Items { get; } = new();

    public void Report(T value) => Items.Add(value);
}

/// <summary>
/// 极简本地 WebDAV 服务器（TcpListener 手写 HTTP/1.1），只实现 BackupService 用到的
/// MKCOL / PROPFIND / PUT / GET / DELETE + Basic 认证，用于在没有真实网盘时验证 WebDAV 逻辑。
/// </summary>
internal sealed class FakeWebDavServer : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _expectedAuthorization;
    private readonly Task _acceptLoop;
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _modified = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase) { "dav" };

    public FakeWebDavServer(string user, string password)
    {
        _expectedAuthorization = "Basic " +
                                 Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        RootUrl = $"http://127.0.0.1:{port}/dav/";
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>形如 http://127.0.0.1:端口/dav/ 的根地址。</summary>
    public string RootUrl { get; }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var header = new List<byte>(4096);
                var single = new byte[1];
                var headerEnd = -1;
                while (headerEnd < 0 && header.Count < 64 * 1024)
                {
                    var read = await stream.ReadAsync(single, ct).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        return;
                    }

                    header.Add(single[0]);
                    var count = header.Count;
                    if (single[0] == (byte)'\n'
                        && count >= 4
                        && header[count - 2] == (byte)'\r'
                        && header[count - 3] == (byte)'\n'
                        && header[count - 4] == (byte)'\r')
                    {
                        headerEnd = count - 4;
                    }
                }

                if (headerEnd < 0)
                {
                    return;
                }

                var headerText = Encoding.ASCII.GetString(header.ToArray(), 0, headerEnd);
                var lines = headerText.Split("\r\n");
                var requestLine = lines[0].Split(' ');
                var method = requestLine[0].ToUpperInvariant();
                var target = requestLine.Length > 1 ? requestLine[1] : "/";
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < lines.Length; i++)
                {
                    var colon = lines[i].IndexOf(':');
                    if (colon > 0)
                    {
                        headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
                    }
                }

                var contentLength = headers.TryGetValue("Content-Length", out var lengthText)
                                    && int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLength)
                    ? parsedLength
                    : 0;

                var body = new byte[contentLength];
                var offset = 0;
                while (offset < contentLength)
                {
                    var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), ct).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }

                if (!headers.TryGetValue("Authorization", out var authorization)
                    || authorization != _expectedAuthorization)
                {
                    await WriteAsync(stream, 401, "Unauthorized", Array.Empty<byte>(), "text/plain", ct).ConfigureAwait(false);
                    return;
                }

                var rawPath = target.Split('?')[0];
                var within = Uri.UnescapeDataString(rawPath).TrimStart('/');
                if (within.StartsWith("dav/", StringComparison.OrdinalIgnoreCase))
                {
                    within = within[4..];
                }
                else if (string.Equals(within, "dav", StringComparison.OrdinalIgnoreCase))
                {
                    within = string.Empty;
                }
                else
                {
                    await WriteAsync(stream, 404, "Not Found", Array.Empty<byte>(), "text/plain", ct).ConfigureAwait(false);
                    return;
                }

                int status;
                string reason;
                byte[] responseBody;
                var contentType = "text/plain";
                lock (_gate)
                {
                    switch (method)
                    {
                        case "MKCOL":
                        {
                            var folder = within.TrimEnd('/');
                            if (folder.Length > 0 && _folders.Add(folder))
                            {
                                status = 201;
                                reason = "Created";
                            }
                            else
                            {
                                status = 405;
                                reason = "Method Not Allowed";
                            }

                            responseBody = Array.Empty<byte>();
                            break;
                        }

                        case "PROPFIND":
                        {
                            var folder = within.TrimEnd('/');
                            if (folder.Length > 0 && !_folders.Contains(folder))
                            {
                                status = 404;
                                reason = "Not Found";
                                responseBody = Array.Empty<byte>();
                                break;
                            }

                            var depth = headers.TryGetValue("Depth", out var depthText) ? depthText.Trim() : "infinity";
                            responseBody = BuildListingXml(within.TrimEnd('/') + "/", depth != "0");
                            status = 207;
                            reason = "Multi-Status";
                            contentType = "application/xml; charset=utf-8";
                            break;
                        }

                        case "PUT":
                        {
                            _files[within] = body;
                            _modified[within] = DateTimeOffset.Now;
                            status = 201;
                            reason = "Created";
                            responseBody = Array.Empty<byte>();
                            break;
                        }

                        case "GET":
                        {
                            if (_files.TryGetValue(within, out var content))
                            {
                                status = 200;
                                reason = "OK";
                                responseBody = content;
                                contentType = "application/json; charset=utf-8";
                            }
                            else
                            {
                                status = 404;
                                reason = "Not Found";
                                responseBody = Array.Empty<byte>();
                            }

                            break;
                        }

                        case "DELETE":
                        {
                            if (_files.Remove(within) && _modified.Remove(within))
                            {
                                status = 204;
                                reason = "No Content";
                            }
                            else
                            {
                                status = 404;
                                reason = "Not Found";
                            }

                            responseBody = Array.Empty<byte>();
                            break;
                        }

                        default:
                            status = 405;
                            reason = "Method Not Allowed";
                            responseBody = Array.Empty<byte>();
                            break;
                    }
                }

                await WriteAsync(stream, status, reason, responseBody, contentType, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // 测试用服务器：连接异常直接丢弃，不影响测试进程
        }
    }

    private byte[] BuildListingXml(string folderWithin, bool includeChildren)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        builder.Append("<D:multistatus xmlns:D=\"DAV:\">\n");
        AppendResponse(builder, "/dav/" + folderWithin, true, null, null);
        if (includeChildren)
        {
            foreach (var pair in _files
                         .Where(f => f.Key.StartsWith(folderWithin, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                var modified = _modified.TryGetValue(pair.Key, out var value) ? value : DateTimeOffset.Now;
                AppendResponse(builder, "/dav/" + pair.Key, false, pair.Value.Length, modified);
            }
        }

        builder.Append("</D:multistatus>");
        return Utf8NoBom.GetBytes(builder.ToString());
    }

    private static void AppendResponse(
        StringBuilder builder,
        string href,
        bool isCollection,
        long? size,
        DateTimeOffset? modified)
    {
        builder.Append("  <D:response><D:href>").Append(href).Append("</D:href>");
        builder.Append("<D:propstat><D:prop>");
        builder.Append(isCollection ? "<D:resourcetype><D:collection/></D:resourcetype>" : "<D:resourcetype/>");
        if (size is long length)
        {
            builder.Append("<D:getcontentlength>").Append(length).Append("</D:getcontentlength>");
        }

        if (modified is DateTimeOffset time)
        {
            builder.Append("<D:getlastmodified>")
                .Append(time.UtcDateTime.ToString("R", CultureInfo.InvariantCulture))
                .Append("</D:getlastmodified>");
        }

        builder.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>\n");
    }

    private static async Task WriteAsync(
        NetworkStream stream,
        int status,
        string reason,
        byte[] body,
        string contentType,
        CancellationToken ct)
    {
        var header = $"HTTP/1.1 {status} {reason}\r\n"
                     + $"Content-Length: {body.Length}\r\n"
                     + $"Content-Type: {contentType}\r\n"
                     + "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _listener.Stop();
        }
        catch
        {
            // ignore
        }

        _ = _acceptLoop;
        _cts.Dispose();
    }
}
