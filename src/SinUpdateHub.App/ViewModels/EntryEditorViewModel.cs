using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.ViewModels;

/// <summary>添加/编辑条目窗口的 ViewModel，含「自动识别」与「测试检查」。</summary>
public sealed class EntryEditorViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly AppEntry? _original;

    private string _name = string.Empty;
    private string _displayName = string.Empty;
    private SourceKind _source = SourceKind.GitHub;
    private string _repoUrl = string.Empty;
    private string _package = string.Empty;
    private bool _packageIsRegex;
    private string _currentVersion = string.Empty;
    private string _installDir = string.Empty;
    private string _exePath = string.Empty;
    private string _installArgs = string.Empty;
    private string _remark = string.Empty;
    private string _homepageUrl = string.Empty;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private bool _isRegexAuto;
    private string _detectHint = string.Empty;
    private string _packageDisplayName = string.Empty;
    private bool _packageAutoFilled;

    private string _testLatest = string.Empty;
    private string _testAsset = string.Empty;
    private string _testAdvisory = string.Empty;
    private bool _testOk;
    private bool _hasExeVersion;
    private bool _suppressInstallDirProbe;
    private int _exeProbeSeq;

    public EntryEditorViewModel(AppServices services, AppEntry? existing, SourceKind defaultSource = SourceKind.GitHub)
    {
        _services = services;
        _original = existing;

        if (existing is not null)
        {
            Title = "编辑软件";
            _name = existing.Name;
            _displayName = existing.DisplayName ?? string.Empty;
            _source = existing.Source;
            _repoUrl = existing.RepoUrl;
            _package = existing.Package;
            _packageIsRegex = existing.PackageIsRegex;
            _currentVersion = existing.CurrentVersion;
            _installDir = existing.InstallDir;
            _exePath = existing.ExePath;
            _installArgs = existing.InstallArgs;
            _remark = existing.Remark;
            _homepageUrl = existing.HomepageUrl ?? string.Empty;
        }
        else
        {
            Title = "添加软件";
            _source = defaultSource;
        }

        AutoDetectCommand = new AsyncRelayCommand(AutoDetectAsync, () => CanAutoDetect);
        ApplyCandidateCommand = new RelayCommand(ApplyCandidate);
        TestCheckCommand = new AsyncRelayCommand(TestCheckAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        BrowseInstallExeCommand = new AsyncRelayCommand(BrowseInstallExeAsync);
        ClearExePathCommand = new RelayCommand(ClearExePath);
        SearchPackageCommand = new RelayCommand(SearchPackage, () => IsWinGet && !IsBusy);
        ClearDetectionCommand = new RelayCommand(() =>
        {
            Candidates.Clear();
            DetectHint = string.Empty;
            TestLatest = string.Empty;
            TestAsset = string.Empty;
            TestAdvisory = string.Empty;
            TestOk = false;
        });
    }

    public event Action<bool>? RequestClose;

    /// <summary>按关键词静默搜索 WinGet 包（用于自动填充包 ID），返回候选列表。</summary>
    public Func<string, Task<IReadOnlyList<WingetPackageInfo>>>? SearchWingetPackagesRequested { get; set; }

    /// <summary>弹出「搜索 WinGet 包」窗口让用户选，返回选中项（取消返回 null）。参数是初始关键词。</summary>
    public Func<string, WingetPackageInfo?>? PickWingetPackageRequested { get; set; }

    public string Title { get; }

    public bool IsNew => _original is null;

    // ---------- 表单字段 ----------

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value) && _packageAutoFilled && IsWinGet)
            {
                // 包 ID 是根据旧名称自动搜出来的：名称一改就清空，避免「改成 Everything 包还是抖音的」
                _packageAutoFilled = false;
                Package = string.Empty;
                PackageDisplayName = string.Empty;
                StatusMessage = "项目名称已修改，原自动匹配的包已清空，请重新点「搜索包 ID」。";
            }
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public SourceKind Source
    {
        get => _source;
        set
        {
            if (SetProperty(ref _source, value))
            {
                RaisePropertyChanged(nameof(IsGitHub), nameof(IsWinGet), nameof(IsCustom), nameof(SourceHint), nameof(PackageLabel), nameof(PackageHint), nameof(ShowRegexOption), nameof(RepoPlaceholder), nameof(ShowPackageSearch), nameof(ShowPackageSection));
                (SearchPackageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsGitHub
    {
        get => _source == SourceKind.GitHub;
        set
        {
            if (value)
            {
                Source = SourceKind.GitHub;
            }
        }
    }

    public bool IsWinGet
    {
        get => _source == SourceKind.WinGet;
        set
        {
            if (value)
            {
                Source = SourceKind.WinGet;
            }
        }
    }

    /// <summary>官网/手动来源（WinGet、GitHub 都没有的软件）。</summary>
    public bool IsCustom
    {
        get => _source == SourceKind.Custom;
        set
        {
            if (value)
            {
                Source = SourceKind.Custom;
            }
        }
    }

    public bool ShowRegexOption => _source == SourceKind.GitHub;

    /// <summary>官网/手动来源不需要「包」字段。</summary>
    public bool ShowPackageSection => _source != SourceKind.Custom;

    public string SourceHint => _source switch
    {
        SourceKind.GitHub =>
            "GitHub 来源：填仓库地址，自动读取 Releases 最新版本，再按「包」筛选 Assets 里的安装包文件名。",
        SourceKind.WinGet =>
            "WinGet 来源：不用记包 ID——填好项目名称后点「搜索包 ID」或「自动识别」，会自动从 WinGet 官方索引里找到包 ID，并读取最新版本与安装包直链（不需要本机装 winget）。",
        _ =>
            "官网/手动来源：WinGet 清单和 GitHub 都没有的软件用这个（如 NVIDIA Broadcast）。填官网下载页；检查时只刷新本机当前版本，「更新」= 打开官网下载页，由你手动下载安装。",
    };

    /// <summary>官网下载页（官网/手动来源用）。</summary>
    public string HomepageUrl
    {
        get => _homepageUrl;
        set => SetProperty(ref _homepageUrl, value);
    }

    public string RepoPlaceholder => "https://github.com/owner/repo";

    public string RepoUrl
    {
        get => _repoUrl;
        set => SetProperty(ref _repoUrl, value);
    }

    public string PackageLabel => _source == SourceKind.GitHub ? "包（Assets 文件名筛选）" : "包（WinGet 包 ID）";

    public string PackageHint => _source == SourceKind.GitHub
        ? "填安装包文件名里的关键字符（如 Setup.exe），或用正则；留空则自动挑最像安装包的那个。支持竖线分隔多个关键字。"
        : "不用记包 ID：留空后点「搜索包 ID」，按软件名称（如 UU远程、微信）自动查找并填入；也可以直接粘贴已知的 ID。";

    /// <summary>WinGet 模式下显示「搜索包 ID」按钮。</summary>
    public bool ShowPackageSearch => _source == SourceKind.WinGet;

    /// <summary>从 WinGet 索引里选中的包显示名。</summary>
    public string PackageDisplayName
    {
        get => _packageDisplayName;
        private set
        {
            if (SetProperty(ref _packageDisplayName, value))
            {
                RaisePropertyChanged(nameof(HasPackageDisplayName));
            }
        }
    }

    public bool HasPackageDisplayName => !string.IsNullOrWhiteSpace(_packageDisplayName);

    public string Package
    {
        get => _package;
        set
        {
            if (SetProperty(ref _package, value))
            {
                _packageAutoFilled = false; // 任何赋值都先视为"不是自动填的"，自动填充逻辑完成后再把标记设回 true
                // 用户没显式勾选正则时，若写法明显是正则（^ 开头、含 | 或 \d 等）就自动按正则解释
                var looksLikeRegex = AssetMatcher.LooksLikeRegex(value);
                if (_isRegexAuto != looksLikeRegex)
                {
                    _isRegexAuto = looksLikeRegex;
                    RaisePropertyChanged(nameof(IsRegexEffective));
                }
            }
        }
    }

    public bool PackageIsRegex
    {
        get => _packageIsRegex;
        set
        {
            if (SetProperty(ref _packageIsRegex, value))
            {
                RaisePropertyChanged(nameof(IsRegexEffective));
            }
        }
    }

    /// <summary>实际是否按正则解释：用户勾选，或（未勾选但看起来像正则且是"自动"模式）。</summary>
    public bool IsRegexEffective => _packageIsRegex || _isRegexAuto;

    public string CurrentVersion
    {
        get => _currentVersion;
        set => SetProperty(ref _currentVersion, value);
    }

    public string InstallDir
    {
        get => _installDir;
        set
        {
            if (SetProperty(ref _installDir, value))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _hasExeVersion = false;
                }
                else if (!_suppressInstallDirProbe)
                {
                    _ = RefreshExeVersionAsync();
                }

                RaisePropertyChanged(nameof(CanAutoDetect));
                (AutoDetectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>绑定的主程序 exe：一旦绑定，当前版本优先从这个文件读。</summary>
    public string ExePath
    {
        get => _exePath;
        set
        {
            if (SetProperty(ref _exePath, value))
            {
                RaisePropertyChanged(nameof(HasExePath), nameof(ExePathText), nameof(CanAutoDetect));
                (AutoDetectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanAutoDetect => !IsBusy && (string.IsNullOrWhiteSpace(InstallDir) || !_hasExeVersion);

    public bool HasExePath => !string.IsNullOrWhiteSpace(_exePath);

    public string ExePathText => string.IsNullOrWhiteSpace(_exePath)
        ? string.Empty
        : "已绑定主程序（版本永远从这里读）：" + _exePath;

    public string InstallArgs
    {
        get => _installArgs;
        set => SetProperty(ref _installArgs, value);
    }

    public string Remark
    {
        get => _remark;
        set => SetProperty(ref _remark, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(CanAutoDetect));
                (SaveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (AutoDetectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (TestCheckCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (SearchPackageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string DetectHint
    {
        get => _detectHint;
        private set => SetProperty(ref _detectHint, value);
    }

    public ObservableCollection<DetectedApp> Candidates { get; } = new();

    public bool HasCandidates => Candidates.Count > 0;

    public string TestLatest
    {
        get => _testLatest;
        private set => SetProperty(ref _testLatest, value);
    }

    public string TestAsset
    {
        get => _testAsset;
        private set => SetProperty(ref _testAsset, value);
    }

    public string TestAdvisory
    {
        get => _testAdvisory;
        private set => SetProperty(ref _testAdvisory, value);
    }

    public bool TestOk
    {
        get => _testOk;
        private set => SetProperty(ref _testOk, value);
    }

    public bool HasTestResult => !string.IsNullOrWhiteSpace(_testLatest);

    // ---------- 命令 ----------

    public ICommand AutoDetectCommand { get; }
    public ICommand ApplyCandidateCommand { get; }
    public ICommand TestCheckCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseInstallExeCommand { get; }
    public ICommand ClearExePathCommand { get; }
    public ICommand ClearDetectionCommand { get; }
    public ICommand SearchPackageCommand { get; }

    // ---------- 逻辑 ----------

    private AppEntry BuildEntry()
    {
        var entry = _original?.Clone() ?? new AppEntry();
        entry.Name = Name.Trim();
        entry.DisplayName = DisplayName.Trim();
        entry.Source = Source;
        entry.RepoUrl = RepoUrl.Trim();
        entry.Package = Package.Trim();
        entry.PackageIsRegex = PackageIsRegex;
        entry.CurrentVersion = CurrentVersion.Trim();
        entry.InstallDir = InstallDir.Trim();
        entry.ExePath = ExePath.Trim();
        entry.InstallArgs = InstallArgs.Trim();
        entry.Remark = Remark.Trim();
        entry.HomepageUrl = string.IsNullOrWhiteSpace(HomepageUrl) ? null : HomepageUrl.Trim();
        return entry;
    }

    /// <summary>点「搜索包 ID」：打开候选窗口，按名称/关键词查本地 WinGet 索引。</summary>
    private void SearchPackage()
    {
        var keyword = !string.IsNullOrWhiteSpace(Package) ? Package.Trim() : Name.Trim();
        if (keyword.Length == 0)
        {
            StatusMessage = "请先填「项目名称」，或在「包」里输入关键词（如 UU、微信、7zip）。";
            return;
        }

        try
        {
            var picked = PickWingetPackageRequested?.Invoke(keyword);
            if (picked is null)
            {
                return;
            }

            Package = picked.Id;
            _packageAutoFilled = true;
            PackageDisplayName = picked.Name;
            StatusMessage = $"已选择：{picked.Name}（{picked.Id}，最新 {picked.Version}）";
        }
        catch (Exception ex)
        {
            AppLog.Write("搜索 WinGet 包失败", ex);
            StatusMessage = "搜索失败：" + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        var entry = BuildEntry();
        var error = Validate(entry);
        if (error is not null)
        {
            StatusMessage = error;
            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.InstallDir))
        {
            var dir = Environment.ExpandEnvironmentVariables(entry.InstallDir.Trim().Trim('"'));
            if (!Directory.Exists(dir))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"无法创建安装目录：{entry.InstallDir}（{ex.Message}）";
                    return;
                }
            }
        }

        IsBusy = true;
        try
        {
            Result = entry;
            RequestClose?.Invoke(true);
        }
        finally
        {
            IsBusy = false;
        }

        await Task.CompletedTask;
    }

    private string? Validate(AppEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            return "请填写项目名称。";
        }

        if (entry.Source == SourceKind.Custom)
        {
            if (string.IsNullOrWhiteSpace(entry.HomepageUrl))
            {
                return "官网/手动来源需要填写「官网下载页」，例如 https://www.nvidia.cn/geforce/broadcasting/broadcast-app/";
            }

            if (!entry.HomepageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return "官网下载页要填完整网址（以 http 或 https 开头）。";
            }
        }

        if (entry.Source == SourceKind.GitHub)
        {
            if (string.IsNullOrWhiteSpace(entry.RepoUrl))
            {
                return "GitHub 来源必须填写仓库地址，例如 https://github.com/owner/repo";
            }

            if (AssetMatcher.ParseGitHubRepo(entry.RepoUrl) is null)
            {
                return "仓库地址无法解析，请填 https://github.com/owner/repo 或 owner/repo";
            }
        }
        else if (entry.Source == SourceKind.WinGet)
        {
            if (string.IsNullOrWhiteSpace(entry.Package))
            {
                return "WinGet 来源必须填写包 ID，例如 NetEase.UURemote";
            }

            if (entry.Package.Contains(' ') || !entry.Package.Contains('.'))
            {
                return "WinGet 包 ID 格式不对，应该形如 NetEase.UURemote。";
            }
        }

        return null;
    }

    /// <summary>打开弹窗或修改安装目录时刷新主程序 EXE 的文件版本，决定是否允许「自动识别」。</summary>
    public async Task RefreshExeVersionAsync()
    {
        var seq = ++_exeProbeSeq;
        var exePath = ExePath?.Trim() ?? string.Empty;
        var installDir = InstallDir?.Trim() ?? string.Empty;

        try
        {
            if (!string.IsNullOrWhiteSpace(exePath)
                && !InstalledAppDetector.IsWindowsAppsAliasPath(exePath)
                && File.Exists(exePath))
            {
                var detected = await _services.Detector.ResolveFromExecutableAsync(exePath);
                if (seq != _exeProbeSeq)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(detected?.Version))
                {
                    _hasExeVersion = true;
                    CurrentVersion = detected.Version;
                }
                else
                {
                    _hasExeVersion = false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
            {
                var detected = await _services.Detector.ResolveExeOnlyFromInstallDirAsync(installDir);
                if (seq != _exeProbeSeq)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(detected?.Version))
                {
                    _hasExeVersion = true;
                    CurrentVersion = detected.Version;
                }
                else
                {
                    _hasExeVersion = false;
                }
            }
            else
            {
                _hasExeVersion = false;
            }
        }
        catch
        {
            if (seq == _exeProbeSeq)
            {
                _hasExeVersion = false;
            }
        }
        finally
        {
            if (seq == _exeProbeSeq)
            {
                RaisePropertyChanged(nameof(CanAutoDetect));
                (AutoDetectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    private async Task AutoDetectAsync()
    {
        IsBusy = true;
        StatusMessage = "正在扫描本机已安装软件…";
        Candidates.Clear();
        RaisePropertyChanged(nameof(HasCandidates));
        try
        {
            var probe = BuildEntry();

            // 已绑定非别名 EXE 且文件不存在：软件尚未安装，不扫注册表也不覆盖绑定路径
            if (!string.IsNullOrWhiteSpace(probe.ExePath)
                && !InstalledAppDetector.IsWindowsAppsAliasPath(probe.ExePath)
                && !File.Exists(probe.ExePath))
            {
                CurrentVersion = string.Empty;
                DetectHint = $"绑定的主程序不存在（软件尚未安装）：{probe.ExePath}";
                StatusMessage = "绑定的主程序文件不存在，软件尚未安装（可保存后直接点「安装」）。";
                return;
            }

            var list = await _services.Detector.FindCandidatesAsync(probe);
            foreach (var item in list)
            {
                Candidates.Add(item);
            }

            RaisePropertyChanged(nameof(HasCandidates));
            DetectHint = Candidates.Count == 0
                ? (!string.IsNullOrWhiteSpace(probe.ExePath)
                    ? "没有找到与绑定软件名称一致的系统记录，请手动填写当前版本。"
                    : "没有自动识别到（需与项目名称或备注一致）。请手动填写当前版本。")
                : $"找到 {Candidates.Count} 个候选，点一行即可回填版本与安装目录。";

            if (Candidates.Count > 0)
            {
                ApplyCandidate(Candidates[0]);
            }

            // WinGet 来源且包 ID 为空：顺手按项目名称去本地索引里自动填包 ID
            var autoPackageMessage = string.Empty;
            if (IsWinGet && string.IsNullOrWhiteSpace(Package))
            {
                autoPackageMessage = await TryAutoFillWingetPackageAsync();
            }

            StatusMessage = (Candidates.Count == 0 ? "自动识别未命中，可手动填写。" : "已自动回填第一个候选，可在下方切换。")
                            + autoPackageMessage;
        }
        catch (Exception ex)
        {
            AppLog.Write("自动识别失败", ex);
            StatusMessage = "自动识别失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>用项目名称去 WinGet 本地索引里找包 ID（唯一的高分匹配才自动填）。</summary>
    private async Task<string> TryAutoFillWingetPackageAsync()
    {
        if (SearchWingetPackagesRequested is null || string.IsNullOrWhiteSpace(Name))
        {
            return string.Empty;
        }

        try
        {
            var list = await SearchWingetPackagesRequested(Name.Trim());
            if (list.Count == 0)
            {
                return "（WinGet 索引里没找到匹配的包，请点「搜索包 ID」换关键词）";
            }

            var best = list[0];
            Package = best.Id;
            _packageAutoFilled = true;
            PackageDisplayName = best.Name;
            return $"（已按名称自动填入 WinGet 包 ID：{best.Id}）";
        }
        catch (Exception ex)
        {
            AppLog.Write("自动填充 WinGet 包 ID 失败", ex);
            return "（自动查找 WinGet 包 ID 失败：" + ex.Message + "）";
        }
    }

    private void ApplyCandidate(object? parameter)
    {
        if (parameter is not DetectedApp app)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(app.Version))
        {
            CurrentVersion = app.Version!;
        }

        if (!string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            _suppressInstallDirProbe = true;
            try
            {
                InstallDir = app.InstallLocation!;
            }
            finally
            {
                _suppressInstallDirProbe = false;
            }
        }

        if (string.IsNullOrWhiteSpace(ExePath))
        {
            var mainExe = InstalledAppDetector.ResolveMainExePath(app);
            if (!string.IsNullOrWhiteSpace(mainExe))
            {
                ExePath = mainExe;
            }
        }

        StatusMessage = string.IsNullOrWhiteSpace(app.Version)
            ? "已回填安装目录（该软件读不到版本号，请手动填当前版本）。"
            : $"已回填：版本 {app.Version}";
    }

    /// <summary>
    /// 选择主程序 exe：自动把安装目录设为 exe 所在目录，并读取该 exe 的文件版本作为「当前版本」，
    /// 同时隐藏之前的自动识别候选结果。
    /// </summary>
    private async Task BrowseInstallExeAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择软件主程序 exe（会自动填安装目录，并读取它的文件版本当当前版本）",
            Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = false,
            DereferenceLinks = false,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(InstallDir) && Directory.Exists(InstallDir))
        {
            dialog.InitialDirectory = InstallDir;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 选择 EXE 后直接隐藏自动识别结果
        Candidates.Clear();
        RaisePropertyChanged(nameof(HasCandidates));
        DetectHint = string.Empty;

        if (InstalledAppDetector.IsWindowsAppsAliasPath(dialog.FileName))
        {
            IsBusy = true;
            StatusMessage = "正在从系统 MSIX/Appx 注册信息识别版本…";
            try
            {
                var detected = await _services.Detector.ResolveFromExecutableAsync(dialog.FileName);
                if (!string.IsNullOrWhiteSpace(detected?.Version))
                {
                    CurrentVersion = detected!.Version!;
                    StatusMessage = $"已识别 MSIX/商店应用「{detected.DisplayName}」，当前版本 {detected.Version}（MSIX 应用无需绑定 EXE，后续会自动从系统识别版本）。";
                }
                else
                {
                    StatusMessage = "所选文件是 WindowsApps 应用执行别名（MSIX/商店应用），无法直接读 EXE 版本；请点「自动识别」或手动填写当前版本。";
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("识别 MSIX/Appx 版本失败", ex);
                StatusMessage = "识别 MSIX/Appx 版本失败：" + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }

            return;
        }

        if (!File.Exists(dialog.FileName))
        {
            StatusMessage = $"文件不存在：{dialog.FileName}";
            return;
        }

        _exeProbeSeq++;
        _suppressInstallDirProbe = true;
        try
        {
            InstallDir = Path.GetDirectoryName(dialog.FileName) ?? InstallDir;
            ExePath = dialog.FileName;
        }
        finally
        {
            _suppressInstallDirProbe = false;
        }

        IsBusy = true;
        StatusMessage = "正在读取 exe 的文件版本…";
        try
        {
            var detected = await _services.Detector.ResolveFromExecutableAsync(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(detected?.Version))
            {
                _hasExeVersion = true;
                CurrentVersion = detected!.Version!;
                StatusMessage = $"已绑定主程序 {Path.GetFileName(dialog.FileName)}，当前版本 {detected.Version}（直接显示 EXE 版本）。";
            }
            else
            {
                _hasExeVersion = false;
                CurrentVersion = string.Empty;
                StatusMessage = "该 EXE 没有文件版本信息，可点下方「自动识别」从系统软件列表获取版本，或手动填写。";
            }
        }
        catch (Exception ex)
        {
            _hasExeVersion = false;
            AppLog.Write("读取 exe 文件版本失败", ex);
            StatusMessage = "读取 exe 版本失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>解除主程序绑定（回到注册表/目录识别）。</summary>
    private void ClearExePath()
    {
        ExePath = string.Empty;
        _hasExeVersion = false;
        RaisePropertyChanged(nameof(CanAutoDetect));
        (AutoDetectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        _ = RefreshExeVersionAsync();
        StatusMessage = "已解除主程序绑定，当前版本改为自动识别。";
    }

    private async Task TestCheckAsync()
    {
        var probe = BuildEntry();
        var error = Validate(probe);
        if (error is not null)
        {
            StatusMessage = error;
            return;
        }

        IsBusy = true;
        StatusMessage = "正在测试检查…";
        TestLatest = string.Empty;
        TestAsset = string.Empty;
        TestAdvisory = string.Empty;
        TestOk = false;
        RaisePropertyChanged(nameof(HasTestResult));
        try
        {
            var result = await _services.Checker.CheckOneAsync(probe);
            if (result.Update is not null)
            {
                TestOk = result.State != UpdateState.Failed;
                TestLatest = result.Update.Version;
                TestAsset = string.IsNullOrWhiteSpace(result.Update.AssetName)
                    ? "（没有可直链下载的安装包）"
                    : result.Update.AssetName!;
                TestAdvisory = result.Update.Advisory ?? string.Empty;
                StatusMessage = TestOk
                    ? $"检查成功：最新版本 {result.Update.Version}，状态 {result.State}"
                    : "检查失败：" + (result.Error ?? "未知原因");
            }
            else
            {
                TestOk = false;
                TestLatest = "—";
                StatusMessage = "检查失败：" + (result.Error ?? "未知原因");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("测试检查失败", ex);
            StatusMessage = "检查失败：" + ex.Message;
            TestOk = false;
            TestLatest = "—";
        }
        finally
        {
            IsBusy = false;
            RaisePropertyChanged(nameof(HasTestResult));
        }
    }

    /// <summary>保存成功后的结果（由窗口读取）。</summary>
    public AppEntry? Result { get; private set; }
}
