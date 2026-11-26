using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.ViewModels;

/// <summary>列表筛选类型。</summary>
public enum AppFilter
{
    All = 0,
    Updatable,
    Latest,
    Failed,
    Unknown,
}

/// <summary>筛选芯片（全部 / 可更新 / 已最新 / 失败 / 未检查）。</summary>
public sealed class FilterChip : ObservableObject
{
    private int _count;
    private bool _isSelected;

    public FilterChip(AppFilter value, string label)
    {
        Value = value;
        Label = label;
    }

    public AppFilter Value { get; }
    public string Label { get; }

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>主窗口右侧显示的页面。</summary>
public enum MainPage
{
    Apps = 0,
    Official,
    Notes,
    BaiduPan,
    Backup,
    Settings,
}

/// <summary>来源筛选芯片（全部来源 / GitHub / WinGet）。</summary>
public sealed class SourceFilterChip : ObservableObject
{
    private bool _isSelected;

    public SourceFilterChip(SourceKind? value, string label)
    {
        Value = value;
        Label = label;
    }

    public SourceKind? Value { get; }
    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>左侧导航项。</summary>
public sealed class NavItem : ObservableObject
{
    private bool _isSelected;

    public NavItem(MainPage page, string label, string glyph)
    {
        Page = page;
        Label = label;
        Glyph = glyph;
    }

    public MainPage Page { get; }
    public string Label { get; }
    public string Glyph { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>主界面 ViewModel：清单列表、统计、检查与更新编排、右侧页面切换。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _autoBackupTimer;

    private MainPage _currentPage = MainPage.Apps;
    private string _searchText = string.Empty;
    private AppFilter _filter = AppFilter.All;
    private SourceKind? _sourceFilter;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private string _statusText = "点「检查全部更新」开始检查";
    private int _totalCount;
    private int _updatableCount;
    private int _latestCount;
    private int _failedCount;
    private int _unknownCount;
    private int _installedCount;
    private int _notInstalledCount;
    private string _lastCheckText = "尚未检查";

    public MainViewModel(AppServices services)
    {
        _services = services;

        SettingsPage = new SettingsViewModel(services);
        NotesPage = new NotesViewModel(services);
        BaiduPanPage = new BaiduPanViewModel(services);
        BackupPage = new BackupViewModel(services);
        BackupPage.BeforeBackup = () => NotesPage.PrepareForBackup();
        BackupPage.DataRestored += ReloadFromStore;
        BackupPage.AutoBackupCompleted += summary =>
        {
            if (!IsBusy)
            {
                StatusText = summary;
            }
        };

        _autoBackupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(15),
        };
        _autoBackupTimer.Tick += async (_, _) => await BackupPage.TryAutoBackupAsync();

        // 备份页 / SinNote 页是内嵌页面（不是独立窗口），必须把主窗口的弹窗委托转发给它们，
        // 否则 Confirm 为 null 时点了会静默无反应。
        BackupPage.ShowMessage = (title, message) => ShowMessage?.Invoke(title, message);
        BackupPage.Confirm = (title, message) => Confirm?.Invoke(title, message) ?? false;
        NotesPage.ShowMessage = (title, message) => ShowMessage?.Invoke(title, message);
        NotesPage.Confirm = (title, message) => Confirm?.Invoke(title, message) ?? false;
        BaiduPanPage.ShowMessage = (title, message) => ShowMessage?.Invoke(title, message);
        BaiduPanPage.Confirm = (title, message) => Confirm?.Invoke(title, message) ?? false;

        CheckAllCommand = new AsyncRelayCommand(CheckAllAsync, () => !IsBusy && Entries.Any(BelongsToCurrentListPage));
        UpdateAllCommand = new AsyncRelayCommand(UpdateAllAsync, () => !IsBusy && UpdatableCount > 0);
        AddEntryCommand = new AsyncRelayCommand(AddEntryAsync);
        OpenSettingsCommand = new AsyncRelayCommand(ShowSettingsAsync);
        OpenBackupCommand = new AsyncRelayCommand(ShowBackupAsync);
        ShowAppsCommand = new RelayCommand(() => SetPage(MainPage.Apps));
        SelectFilterCommand = new RelayCommand(SelectFilter);
        SelectSourceCommand = new RelayCommand(SelectSource);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        RefreshCommand = new RelayCommand(ReloadFromStore);

        NavItems = new ObservableCollection<NavItem>
        {
            new(MainPage.Apps, "软件列表", "\uE8FD"),
            new(MainPage.Official, "软件下载", "\uE774"),
            new(MainPage.Notes, "SinNote", "\uE70B"),
            new(MainPage.BaiduPan, "百度网盘", "\uE8B7"),
            new(MainPage.Backup, "备份与恢复", "\uE753"),
            new(MainPage.Settings, "设置", "\uE713"),
        };
        NavigateCommand = new AsyncRelayCommand(NavigateAsync);
        NavItems[0].IsSelected = true;

        Filters = new ObservableCollection<FilterChip>
        {
            new(AppFilter.All, "全部"),
            new(AppFilter.Updatable, "可更新"),
            new(AppFilter.Latest, "已最新"),
            new(AppFilter.Failed, "失败"),
            new(AppFilter.Unknown, "未检查"),
        };
        Filters[0].IsSelected = true;

        SourceFilters = new ObservableCollection<SourceFilterChip>
        {
            new(null, "全部来源"),
            new(SourceKind.GitHub, "GitHub"),
            new(SourceKind.WinGet, "WinGet"),
        };
        SourceFilters[0].IsSelected = true;

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = FilterPredicate;

        ReloadFromStore();
    }

    /// <summary>右侧页面切换事件（窗口据此把焦点放到对应控件）。</summary>
    public event Action<MainPage>? PageChanged;

    // ---------- 界面回调（由 MainWindow 注入，VM 不直接碰对话框） ----------

    /// <summary>请求打开"添加/编辑"窗口；传 null 表示新增。返回编辑结果，取消返回 null。</summary>
    public Func<AppEntry?, SourceKind, AppEntry?>? EditEntryRequested { get; set; }

    /// <summary>请求在弹出的候选列表里选一个安装包；返回选中项，取消返回 null。</summary>
    public Func<AvailableUpdate, AssetCandidate?>? ChooseAssetRequested { get; set; }

    public Action<string, string>? ShowMessage { get; set; }

    public Func<string, string, bool>? Confirm { get; set; }

    // ---------- 页面 ----------

    public SettingsViewModel SettingsPage { get; }

    public NotesViewModel NotesPage { get; }

    public BaiduPanViewModel BaiduPanPage { get; }

    public BackupViewModel BackupPage { get; }

    public MainPage CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (_currentPage == MainPage.Notes && value != MainPage.Notes)
            {
                NotesPage.FlushPendingSave();
            }

            if (SetProperty(ref _currentPage, value))
            {
                RaisePropertyChanged(
                    nameof(IsAppsPage),
                    nameof(IsOfficialPage),
                    nameof(IsListPage),
                    nameof(IsNotesPage),
                    nameof(IsBaiduPanPage),
                    nameof(IsBackupPage),
                    nameof(IsSettingsPage),
                    nameof(ShowSourceFilters),
                    nameof(PageTitle),
                    nameof(PageSubtitle));
                foreach (var item in NavItems)
                {
                    item.IsSelected = item.Page == value;
                }

                if (value is MainPage.Apps or MainPage.Official)
                {
                    if (value == MainPage.Official && _filter != AppFilter.All && Filters.Count > 0)
                    {
                        _filter = AppFilter.All;
                        foreach (var chip in Filters)
                        {
                            chip.IsSelected = chip.Value == AppFilter.All;
                        }
                    }

                    RefreshStats();
                }
                else if (value == MainPage.Backup)
                {
                    BackupPage.RefreshSummaryTexts();
                }

                PageChanged?.Invoke(value);
            }
        }
    }

    public bool IsAppsPage => _currentPage == MainPage.Apps;
    public bool IsOfficialPage => _currentPage == MainPage.Official;
    public bool IsListPage => _currentPage is MainPage.Apps or MainPage.Official;
    public bool IsNotesPage => _currentPage == MainPage.Notes;
    public bool IsBaiduPanPage => _currentPage == MainPage.BaiduPan;
    public bool IsBackupPage => _currentPage == MainPage.Backup;
    public bool IsSettingsPage => _currentPage == MainPage.Settings;
    public bool ShowSourceFilters => _currentPage == MainPage.Apps;

    public ObservableCollection<NavItem> NavItems { get; }

    public ICommand NavigateCommand { get; }

    private async Task NavigateAsync(object? parameter)
    {
        if (parameter is not NavItem item)
        {
            return;
        }

        SetPage(item.Page);
        if (item.Page == MainPage.Settings)
        {
            try
            {
                await SettingsPage.OnShownAsync();
            }
            catch (Exception ex)
            {
                AppLog.Write("刷新设置页状态失败", ex);
            }
        }
    }

    public string PageTitle => _currentPage switch
    {
        MainPage.Official => "软件下载",
        MainPage.Notes => "SinNote",
        MainPage.Backup => "备份与恢复",
        MainPage.Settings => "设置",
        _ => "软件列表",
    };

    public string PageSubtitle => _currentPage == MainPage.Official
        ? "软件官网 / 网盘手动下载列表"
        : AppSubtitle;

    private bool BelongsToCurrentListPage(EntryViewModel vm)
        => _currentPage == MainPage.Official
            ? vm.Entry.Source == SourceKind.Custom
            : vm.Entry.Source != SourceKind.Custom;

    private void SetPage(MainPage page) => CurrentPage = page;

    private async Task ShowSettingsAsync()
    {
        SetPage(MainPage.Settings);
        try
        {
            await SettingsPage.OnShownAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("打开设置页失败", ex);
        }
    }

    private async Task ShowBackupAsync()
    {
        SetPage(MainPage.Backup);
        await Task.CompletedTask;
    }

    // ---------- 数据 ----------

    public ObservableCollection<EntryViewModel> Entries { get; } = new();

    public ICollectionView EntriesView { get; }

    public ObservableCollection<FilterChip> Filters { get; }

    public ObservableCollection<SourceFilterChip> SourceFilters { get; }

    public bool IsEmpty => !Entries.Any(BelongsToCurrentListPage);

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                EntriesView.Refresh();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(IsNotBusy));
                (CheckAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (UpdateAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNotBusy => !_isBusy;

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string LastCheckText
    {
        get => _lastCheckText;
        private set => SetProperty(ref _lastCheckText, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    public int UpdatableCount
    {
        get => _updatableCount;
        private set
        {
            if (SetProperty(ref _updatableCount, value))
            {
                RaisePropertyChanged(nameof(UpdateBadgeText), nameof(HasUpdatable));
                (UpdateAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasUpdatable => _updatableCount > 0;

    public string UpdateBadgeText => _updatableCount > 0 ? $"{_updatableCount} 个可更新" : "全部已是最新";

    public int LatestCount
    {
        get => _latestCount;
        private set => SetProperty(ref _latestCount, value);
    }

    public int FailedCount
    {
        get => _failedCount;
        private set => SetProperty(ref _failedCount, value);
    }

    public int UnknownCount
    {
        get => _unknownCount;
        private set => SetProperty(ref _unknownCount, value);
    }

    public int InstalledCount
    {
        get => _installedCount;
        private set => SetProperty(ref _installedCount, value);
    }

    public int NotInstalledCount
    {
        get => _notInstalledCount;
        private set => SetProperty(ref _notInstalledCount, value);
    }

    public string AppTitle => "SinUpdateHub";

    public string AppSubtitle => "GitHub · WinGet 软件更新管理器";

    // ---------- 命令 ----------

    public ICommand CheckAllCommand { get; }
    public ICommand UpdateAllCommand { get; }
    public ICommand AddEntryCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenBackupCommand { get; }
    public ICommand ShowAppsCommand { get; }
    public ICommand SelectFilterCommand { get; }
    public ICommand SelectSourceCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand RefreshCommand { get; }

    // ---------- 生命周期 ----------

    /// <summary>从磁盘重新装载清单。</summary>
    public void ReloadFromStore()
    {
        _services.Data = _services.AppStore.Load();

        Entries.Clear();
        foreach (var entry in _services.Data.Entries)
        {
            Entries.Add(new EntryViewModel(this, entry));
        }

        RefreshStats();
        NotesPage.ReloadFromStore();
        BackupPage.RefreshSummaryTexts();
        RaisePropertyChanged(nameof(IsEmpty));
        (CheckAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>窗口加载后调用：先按绑定 exe 刷新版本，再按设置决定是否启动时自动检查。</summary>
    public async Task InitializeAsync()
    {
        // 启动时刷新所有条目的当前版本（绑定 exe 的读 exe，其他读目录/注册表），保证表格显示的是本机真实版本
        if (Entries.Count > 0)
        {
            var changed = false;
            var scanned = false;
            foreach (var vm in Entries)
            {
                if (!scanned)
                {
                    await _services.Detector.ScanInstalledAsync(forceRefresh: true);
                    scanned = true;
                }

                var before = vm.Entry.CurrentVersion;
                await RefreshCurrentVersionFromInstalledAsync(vm, forceScan: false, clearWhenMissing: true);
                if (!string.Equals(before, vm.Entry.CurrentVersion, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                Persist();
                RefreshStats();
            }
        }

        if (_services.Settings.AutoCheckOnStartup && Entries.Count > 0)
        {
            await CheckAllAsync();
        }

        // 启动 WebDAV 自动定时备份检查器（每 15 分钟轮询是否到期；启动后延迟数秒先检查一次）
        if (!_autoBackupTimer.IsEnabled)
        {
            _autoBackupTimer.Start();
            _ = TriggerStartupAutoBackupCheckAsync();
        }
    }

    private async Task TriggerStartupAutoBackupCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            await BackupPage.TryAutoBackupAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("启动自动定时备份检查失败", ex);
        }
    }

    // ---------- 检查更新 ----------

    public async Task CheckAllAsync()
    {
        var pageEntries = Entries.Where(BelongsToCurrentListPage).ToList();
        if (IsBusy || pageEntries.Count == 0)
        {
            return;
        }

        IsBusy = true;
        BusyText = $"正在检查 {pageEntries.Count} 个软件…";
        StatusText = "正在检查更新…";

        try
        {
            // 检查前先刷新一次本机扫描，确保"当前版本"是最新的（例如你刚手动升级或卸载过软件）
            await _services.Detector.ScanInstalledAsync(forceRefresh: true);

            var uninstalledCount = 0;
            var dualMap = new Dictionary<string, (string? ExeVer, string? SoftVer)>(StringComparer.Ordinal);
            foreach (var vm in pageEntries)
            {
                var hadVersionBefore = !string.IsNullOrWhiteSpace(vm.Entry.CurrentVersion);
                await RefreshCurrentVersionFromInstalledAsync(vm, forceScan: false, clearWhenMissing: true);
                dualMap[vm.Entry.Id] = await DetectDualVersionsAsync(vm, forceScan: false);
                if (hadVersionBefore && string.IsNullOrWhiteSpace(vm.Entry.CurrentVersion))
                {
                    uninstalledCount++;
                }

                vm.MarkChecking();
            }

            var targets = pageEntries.Select(e => e.Entry.Clone()).ToList();
            var appliedIds = new HashSet<string>(StringComparer.Ordinal);
            var progress = new Progress<CheckResult>(result =>
            {
                var vm = pageEntries.FirstOrDefault(e => e.Entry.Id == result.EntryId);
                if (vm is not null)
                {
                    appliedIds.Add(result.EntryId);
                    if (dualMap.TryGetValue(result.EntryId, out var dv))
                    {
                        ReconcileResultWithDualVersions(vm, result, dv.ExeVer, dv.SoftVer);
                    }

                    vm.ApplyCheckResult(result);
                }
            });

            var results = await _services.Checker.CheckAllAsync(targets, progress);

            foreach (var result in results)
            {
                var vm = pageEntries.FirstOrDefault(e => e.Entry.Id == result.EntryId);
                if (vm is not null)
                {
                    if (appliedIds.Add(result.EntryId))
                    {
                        if (dualMap.TryGetValue(result.EntryId, out var dv))
                        {
                            ReconcileResultWithDualVersions(vm, result, dv.ExeVer, dv.SoftVer);
                        }

                        vm.ApplyCheckResult(result);
                    }

                    UpdateEntryReference(vm);
                }
            }

            Persist();
            RefreshStats();
            var missingCount = Math.Max(uninstalledCount, pageEntries.Count(e => e.IsNotInstalled));
            var uninstalledSuffix = missingCount > 0
                ? $"（其中 {missingCount} 个软件尚未安装或未检测到）"
                : string.Empty;
            StatusText = (UpdatableCount > 0
                ? $"检查完成：{UpdatableCount} 个软件有新版本"
                : "检查完成：全部已是最新") + uninstalledSuffix;
        }
        catch (Exception ex)
        {
            AppLog.Write("批量检查更新失败", ex);
            StatusText = "检查失败：" + ex.Message;
        }
        finally
        {
            foreach (var vm in Entries.Where(e => e.State == UpdateState.Checking))
            {
                vm.EvaluateState();
            }

            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    public async Task CheckOneAsync(EntryViewModel vm)
    {
        if (IsBusy)
        {
            return;
        }

        var hadVersionBefore = !string.IsNullOrWhiteSpace(vm.Entry.CurrentVersion);

        // 检查前先刷新"当前版本"：绑定 exe → 读 exe；否则查安装目录 exe → 注册表/MSIX；本机均不存在则清空旧版本
        await RefreshCurrentVersionFromInstalledAsync(vm, forceScan: true, clearWhenMissing: true);
        var (exeVer, softVer) = await DetectDualVersionsAsync(vm, forceScan: false);

        vm.MarkChecking();
        StatusText = $"正在检查「{vm.Name}」…";
        try
        {
            var result = await _services.Checker.CheckOneAsync(vm.Entry.Clone());
            ReconcileResultWithDualVersions(vm, result, exeVer, softVer);
            vm.ApplyCheckResult(result);
            UpdateEntryReference(vm);
            Persist();
            RefreshStats();

            var missingLocally = string.IsNullOrWhiteSpace(vm.Entry.CurrentVersion);
            if (vm.IsNotInstalled)
            {
                if (result.State == UpdateState.UpdateAvailable)
                {
                    StatusText = $"「{vm.Name}」软件尚未安装，远端最新 {result.Update?.Version}，可点「安装」直接安装";
                }
                else if (vm.Entry.Source == SourceKind.Custom)
                {
                    StatusText = result.State == UpdateState.Failed
                        ? $"「{vm.Name}」检查失败：{result.Error}"
                        : $"「{vm.Name}」软件尚未安装，可点「打开网页」下载安装";
                }
                else
                {
                    StatusText = result.State == UpdateState.Failed
                        ? $"「{vm.Name}」检查失败：{result.Error}"
                        : $"「{vm.Name}」软件尚未安装";
                }
            }
            else if (missingLocally && result.State == UpdateState.UpdateAvailable)
            {
                StatusText = hadVersionBefore
                    ? $"「{vm.Name}」本机未检测到已安装版本（可能已卸载），远端最新 {result.Update?.Version}，可点「更新」安装"
                    : $"「{vm.Name}」本机未检测到已安装版本，远端最新 {result.Update?.Version}（可点「更新」安装）";
            }
            else if (missingLocally && hadVersionBefore && vm.Entry.Source == SourceKind.Custom)
            {
                StatusText = $"「{vm.Name}」本机未检测到已安装版本（可能已卸载）";
            }
            else
            {
                StatusText = result.State switch
                {
                    UpdateState.UpdateAvailable => $"「{vm.Name}」有新版本 {result.Update?.Version}",
                    UpdateState.UpToDate => $"「{vm.Name}」已是最新版本",
                    UpdateState.Failed => $"「{vm.Name}」检查失败：{result.Error}",
                    _ => StatusText,
                };
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("单条检查失败", ex);
            vm.SetState(UpdateState.Failed);
            StatusText = $"「{vm.Name}」检查失败：{ex.Message}";
        }
    }

    // ---------- 更新 ----------

    public async Task UpdateOneAsync(EntryViewModel vm)
    {
        // 手动/软件下载来源：没有自动下载链接，直接打开网页
        if (vm.Entry.Source == SourceKind.Custom)
        {
            var page = ResolvePageUrl(vm);
            if (string.IsNullOrWhiteSpace(page))
            {
                ShowMessage?.Invoke("没有网页地址", $"「{vm.Name}」还没有填网页下载地址，请点「编辑」补上。");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = page, UseShellExecute = true });
                StatusText = $"已打开「{vm.Name}」下载网页：{page}";
            }
            catch (Exception ex)
            {
                AppLog.Write("打开网页失败", ex);
                ShowMessage?.Invoke("打开网页失败", ex.Message);
            }

            return;
        }

        if (vm.State != UpdateState.UpdateAvailable)
        {
            ShowMessage?.Invoke("无需更新", $"「{vm.Name}」当前没有可更新的版本。");
            return;
        }

        if (string.IsNullOrWhiteSpace(vm.Entry.DownloadUrl))
        {
            await CheckOneAsync(vm);
            if (string.IsNullOrWhiteSpace(vm.Entry.DownloadUrl))
            {
                ShowMessage?.Invoke("没有下载地址", $"「{vm.Name}」没有解析到可下载的安装包。\n可尝试点「改选安装包」重新匹配。");
                return;
            }
        }

        var latest = vm.Entry.LatestVersion ?? string.Empty;
        var fileName = !string.IsNullOrWhiteSpace(vm.Entry.MatchedAssetName)
            ? vm.Entry.MatchedAssetName!
            : AssetMatcher.FileNameFromUrl(vm.Entry.DownloadUrl!);

        vm.MarkDownloading(true);
        StatusText = $"正在下载「{vm.Name}」的安装包…";
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                var text = p.Received == 0 && !string.IsNullOrWhiteSpace(p.Message)
                    ? p.Message!
                    : p.Total is > 0
                        ? $"下载中 {p.Fraction:P0}  {FormatSize(p.Received)} / {FormatSize(p.Total.Value)}"
                        : $"下载中 {FormatSize(p.Received)}";
                vm.SetProgress(p.Fraction, text);
            });

            var oldUrl = vm.Entry.DownloadUrl!;
            string localPath;
            try
            {
                localPath = await _services.Downloader.DownloadAsync(oldUrl, fileName, progress);
            }
            catch (DownloadRejectedException ex)
            {
                // 签名下载链接过期（401/403/404）：重新解析一次最新地址再重试
                AppLog.Write($"下载被拒绝（HTTP {ex.StatusCode}），尝试重新解析最新下载地址：{vm.Name}", ex);
                StatusText = $"「{vm.Name}」下载链接被服务器拒绝（HTTP {ex.StatusCode}），正在重新获取最新地址…";

                var refreshed = await TryRefreshDownloadUrlAsync(vm);
                var newUrl = vm.Entry.DownloadUrl;
                if (!refreshed || string.IsNullOrWhiteSpace(newUrl)
                    || string.Equals(newUrl, oldUrl, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"下载链接已失效（HTTP {ex.StatusCode}）：上游清单里这个地址被服务器拒绝，重新检查也没拿到新地址。\n\n"
                        + "点「确定」后会自动打开下载页面，你可以手动下载安装；也可以等 WinGet 清单更新后再试。");
                }

                fileName = string.IsNullOrWhiteSpace(vm.Entry.MatchedAssetName)
                    ? AssetMatcher.FileNameFromUrl(newUrl!)
                    : vm.Entry.MatchedAssetName!;
                StatusText = $"「{vm.Name}」已获取新下载地址，重新下载…";
                localPath = await _services.Downloader.DownloadAsync(newUrl!, fileName, progress);
            }
            vm.MarkDownloading(false);
            StatusText = $"「{vm.Name}」安装包已下载：{localPath}";
            AppLog.Write($"下载完成：{vm.Name} → {localPath}");

            // 安装目录为空/不存在（还没装、或绿色软件）：创建目录并打开它；
            // 如果下载的是压缩包（zip/7z/rar），再另外打开下载目录，方便解压后放进安装目录。
            var installDir = vm.Entry.InstallDir;
            if (!string.IsNullOrWhiteSpace(installDir) && IsDirectoryEmptyOrMissing(installDir))
            {
                try
                {
                    Directory.CreateDirectory(installDir);
                    OpenFolderInExplorer(installDir);
                    AppLog.Write($"安装目录为空，已创建并打开：{installDir}");

                    if (IsArchiveFile(localPath))
                    {
                        var downloadFolder = Path.GetDirectoryName(localPath);
                        if (!string.IsNullOrWhiteSpace(downloadFolder))
                        {
                            OpenFolderInExplorer(downloadFolder);
                        }

                        StatusText = $"「{vm.Name}」安装目录为空，已创建并打开安装目录；下载的是压缩包，下载目录也已打开，请解压后放到安装目录";
                        return;
                    }

                    StatusText = $"「{vm.Name}」安装目录为空，已创建并打开安装目录：{installDir}（接下来运行安装器，可把该路径粘贴到安装位置）";
                }
                catch (Exception ex)
                {
                    AppLog.Write("创建/打开安装目录失败", ex);
                    StatusText = $"「{vm.Name}」安装目录：{installDir}（创建/打开失败：{ex.Message}）";
                }
            }

            // 下载完直接运行安装器（不再询问「是否立即运行安装器」）
            var installArgs = vm.Entry.InstallArgs;
            var extension = Path.GetExtension(localPath);
            var isMsi = extension.Equals(".msi", StringComparison.OrdinalIgnoreCase);
            var isAppx = extension.Equals(".msix", StringComparison.OrdinalIgnoreCase)
                         || extension.Equals(".msixbundle", StringComparison.OrdinalIgnoreCase)
                         || extension.Equals(".appx", StringComparison.OrdinalIgnoreCase)
                         || extension.Equals(".appxbundle", StringComparison.OrdinalIgnoreCase);
            var watchProcessName = isMsi ? "msiexec" : isAppx ? "AppInstaller" : Path.GetFileNameWithoutExtension(localPath);
            var currentSessionOnly = isMsi || isAppx;
            var beforePids = SnapshotProcessIds(watchProcessName, currentSessionOnly: currentSessionOnly);
            StatusText = $"正在运行「{vm.Name}」安装器，请按向导完成安装…";
            var result = await _services.Installer.RunAsync(localPath, installArgs, _services.Settings.RunInstallerAsAdmin);
            if (!result.Launched)
            {
                var launchPage = ResolvePageUrl(vm);
                ShowMessage?.Invoke(
                    "无法启动安装器",
                    (result.Error ?? "未知原因") + (string.IsNullOrWhiteSpace(launchPage)
                        ? string.Empty
                        : "\n\n点「确定」后会自动打开下载页面，可手动下载安装。"));
                if (!string.IsNullOrWhiteSpace(launchPage))
                {
                    OpenDownloadPageQuietly(vm);
                }

                return;
            }

            // 压缩包这类需要用户手动处理的：只打开所在目录，不等待、不自动改版本
            if (result.NeedsManualAction)
            {
                AppLog.Write($"需要手动处理：{vm.Name} → {localPath}");
                StatusText = $"「{vm.Name}」下载的是压缩包/手动安装类型，已打开所在文件夹；请手动解压或安装，之后点「检查」刷新版本";
                return;
            }

            // 安装器启动后：非提权模式 InstallerRunner 已等待退出；提权模式再轮询安装进程与本机版本
            var oldVersion = vm.Entry.CurrentVersion;
            var latestVersion = vm.Entry.LatestVersion ?? string.Empty;
            string? detectedVersion;
            if (result.WaitedForExit)
            {
                detectedVersion = await TryRefreshInstalledVersionAsync(vm, oldVersion, latestVersion);
                if (string.IsNullOrWhiteSpace(detectedVersion) && string.IsNullOrWhiteSpace(result.Error))
                {
                    await Task.Delay(1500);
                    detectedVersion = await TryRefreshInstalledVersionAsync(vm, oldVersion, latestVersion);
                }
            }
            else
            {
                StatusText = $"「{vm.Name}」安装器已启动，正在等待安装完成…";
                detectedVersion = await WaitAndDetectInstalledVersionAsync(
                    vm,
                    watchProcessName,
                    beforePids,
                    currentSessionOnly,
                    oldVersion,
                    latestVersion);
            }

            if (!string.IsNullOrWhiteSpace(detectedVersion))
            {
                vm.Entry.CurrentVersion = detectedVersion!;
                vm.Entry.LastError = null;
                vm.RefreshFromEntry();
                vm.EvaluateState();
                UpdateEntryReference(vm);
                Persist();
                RefreshStats();
                StatusText = $"「{vm.Name}」安装完成，当前版本已刷新为 {detectedVersion}";
            }
            else if (result.WaitedForExit && !string.IsNullOrWhiteSpace(result.Error))
            {
                StatusText = $"「{vm.Name}」{result.Error}";
            }
            else if (result.WaitedForExit)
            {
                StatusText = $"「{vm.Name}」安装器已退出；若安装已完成，点该行「检查」可刷新版本";
            }
            else
            {
                StatusText = $"「{vm.Name}」安装器已启动；若安装已完成，点该行「检查」可刷新版本";
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("更新失败", ex);
            vm.MarkDownloading(false);

            // 失败后：弹框提示，用户点「确定」后再自动打开下载页面（官网 / Releases），方便手动下载
            var pageUrl = ResolvePageUrl(vm);
            var suffix = string.IsNullOrWhiteSpace(pageUrl)
                ? string.Empty
                : "\n\n点「确定」后会自动打开下载页面，可手动下载安装。";
            ShowMessage?.Invoke("更新失败", ex.Message + suffix);

            if (!string.IsNullOrWhiteSpace(pageUrl))
            {
                OpenDownloadPageQuietly(vm);
            }

            StatusText = $"「{vm.Name}」更新失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 安装器启动后轮询：既看安装器进程是否结束，也每 3 秒强制重扫一次本机实际版本；
    /// 一旦读到比原版本新的版本就立刻回填（不必等安装器完全退出，也不依赖进程名是否匹配）。
    /// </summary>
    /// <summary>
    /// 重新解析一次某条目的最新下载地址（清缓存后直接问来源），用于签名链接过期后的重试。
    /// </summary>
    private async Task<bool> TryRefreshDownloadUrlAsync(EntryViewModel vm)
    {
        try
        {
            IUpdateSourceProvider provider;
            if (vm.Entry.Source == SourceKind.GitHub)
            {
                _services.GitHub.ClearCache();
                provider = _services.GitHub;
            }
            else
            {
                _services.Winget.ClearCache();
                provider = _services.Winget;
            }

            var update = await provider.GetLatestAsync(vm.Entry);
            vm.Entry.LatestVersion = update.Version;
            vm.Entry.DownloadUrl = update.DownloadUrl;
            vm.Entry.MatchedAssetName = update.AssetName;
            vm.Entry.ReleasePageUrl = update.ReleasePageUrl;
            if (!string.IsNullOrWhiteSpace(update.HomepageUrl))
            {
                vm.Entry.HomepageUrl = update.HomepageUrl;
            }

            vm.Entry.LastCheckedAt = DateTimeOffset.Now;
            vm.Entry.LastError = null;
            vm.RefreshFromEntry();
            UpdateEntryReference(vm);
            Persist();
            return !string.IsNullOrWhiteSpace(update.DownloadUrl);
        }
        catch (Exception ex)
        {
            AppLog.Write("重新解析下载地址失败", ex);
            return false;
        }
    }


    private async Task<string?> WaitAndDetectInstalledVersionAsync(
        EntryViewModel vm,
        string? watchProcessName,
        HashSet<int> beforePids,
        bool currentSessionOnly,
        string oldVersion,
        string latestVersion)
    {
        var deadline = DateTimeOffset.Now.AddMinutes(10);
        var everSawInstaller = false;
        var idleTicks = 0;

        await Task.Delay(2000).ConfigureAwait(true);

        while (DateTimeOffset.Now < deadline)
        {
            var running = HasNewRunningProcess(watchProcessName, beforePids, currentSessionOnly);

            if (running)
            {
                everSawInstaller = true;
                idleTicks = 0;
            }
            else
            {
                idleTicks++;
            }

            var detected = await TryRefreshInstalledVersionAsync(vm, oldVersion, latestVersion);
            if (!string.IsNullOrWhiteSpace(detected))
            {
                return detected;
            }

            // 安装器退出后（或一直没看到它）再兜底读两次；都没有新版本就结束轮询
            var finished = everSawInstaller ? idleTicks >= 2 : idleTicks >= 5;
            if (finished)
            {
                return await TryRefreshInstalledVersionAsync(vm, oldVersion, latestVersion);
            }

            await Task.Delay(3000).ConfigureAwait(true);
        }

        return null;
    }

    private static HashSet<int> SnapshotProcessIds(string? processName, bool currentSessionOnly)
        => GetMatchingProcessIds(processName, currentSessionOnly);

    private static bool HasNewRunningProcess(string? processName, HashSet<int> beforePids, bool currentSessionOnly)
        => GetMatchingProcessIds(processName, currentSessionOnly).Any(id => !beforePids.Contains(id));

    private static HashSet<int> GetMatchingProcessIds(string? processName, bool currentSessionOnly)
    {
        var ids = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(processName))
        {
            return ids;
        }

        var currentSessionId = -1;
        if (currentSessionOnly)
        {
            using var currentProcess = Process.GetCurrentProcess();
            currentSessionId = currentProcess.SessionId;
        }

        Process[]? processes = null;
        try
        {
            processes = Process.GetProcessesByName(processName);
            foreach (var process in processes)
            {
                if (currentSessionOnly && !IsInSession(process, currentSessionId))
                {
                    continue;
                }

                ids.Add(process.Id);
            }
        }
        catch
        {
            // 忽略权限问题
        }
        finally
        {
            DisposeProcesses(processes);
        }

        return ids;
    }

    private static bool IsInSession(Process process, int targetSessionId)
    {
        try
        {
            return process.SessionId == targetSessionId;
        }
        catch
        {
            return false;
        }
    }

    private static void DisposeProcesses(Process[]? processes)
    {
        if (processes is null)
        {
            return;
        }

        foreach (var process in processes)
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>安装目录不存在或里面一个文件都没有（视为"还没安装/绿色软件"）。</summary>
    private static bool IsDirectoryEmptyOrMissing(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                return true;
            }

            return !Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用资源管理器打开某个目录。</summary>
    private static void OpenFolderInExplorer(string folder)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "\"" + folder + "\"",
            UseShellExecute = true,
        });
    }

    /// <summary>是不是压缩包（zip/7z/rar 等）——这类下载完需要手动解压。</summary>
    private static bool IsArchiveFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".tar", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".gz", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".tgz", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 刷新条目的「当前版本」：绑定 exe → 读该 exe；
    /// 否则优先用**注册表卸载项的 DisplayVersion**（与「自动识别」、远端清单口径一致）；
    /// 注册表里没有（绿色/便携软件）才回退到安装目录里 exe 的文件版本。
    /// 检查更新前、更新完成后、启动时都会调用。
    /// </summary>
    private async Task<string?> RefreshCurrentVersionFromInstalledAsync(
        EntryViewModel vm,
        bool forceScan,
        bool clearWhenMissing = false)
    {
        // 1) 绑定过主程序 exe：以它为准
        if (!string.IsNullOrWhiteSpace(vm.Entry.ExePath))
        {
            return await RefreshVersionFromBoundExeAsync(vm, persistWhenChanged: false, clearWhenMissing: clearWhenMissing);
        }

        string? detected = null;

        // 2) 安装目录里的 exe 文件版本优先
        if (!string.IsNullOrWhiteSpace(vm.Entry.InstallDir)
            && Directory.Exists(vm.Entry.InstallDir))
        {
            var fromDir = await _services.Detector.ResolveFromInstallDirAsync(vm.Entry.InstallDir);
            detected = fromDir?.Version;
        }

        // 3) 目录未命中或未设目录 → 注册表卸载项 / MSIX 兜底
        if (string.IsNullOrWhiteSpace(detected))
        {
            if (forceScan)
            {
                await _services.Detector.ScanInstalledAsync(forceRefresh: true);
            }

            var candidates = await _services.Detector.FindCandidatesAsync(vm.Entry);
            detected = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Version) && c.Score >= 30)?.Version;
        }

        if (string.IsNullOrWhiteSpace(detected))
        {
            if (clearWhenMissing && !string.IsNullOrWhiteSpace(vm.Entry.CurrentVersion))
            {
                // 绿色/便携软件目录仍存在且非空（只是 exe 无版本信息）时，保留用户手填版本
                var hasExistingInstallDir = !string.IsNullOrWhiteSpace(vm.Entry.InstallDir)
                                            && !IsDirectoryEmptyOrMissing(vm.Entry.InstallDir);
                if (!hasExistingInstallDir)
                {
                    vm.Entry.CurrentVersion = string.Empty;
                    vm.RefreshFromEntry();
                    vm.EvaluateState();
                    UpdateEntryReference(vm);
                    AppLog.Write($"本机未检测到已安装版本（可能已卸载），已清空当前版本：{vm.Name}");
                }
            }

            return null;
        }

        if (!string.Equals(vm.Entry.CurrentVersion, detected, StringComparison.OrdinalIgnoreCase))
        {
            vm.Entry.CurrentVersion = detected!;
            vm.RefreshFromEntry();
            vm.EvaluateState();
            UpdateEntryReference(vm);
            AppLog.Write($"刷新当前版本：{vm.Name} → {detected}");
        }

        return detected;
    }

    private async Task<(string? ExeVersion, string? SoftwareVersion)> DetectDualVersionsAsync(
        EntryViewModel vm,
        bool forceScan)
    {
        string? exeVersion = null;
        var exePath = vm.Entry.ExePath;
        if (!string.IsNullOrWhiteSpace(exePath) && !InstalledAppDetector.IsWindowsAppsAliasPath(exePath))
        {
            if (File.Exists(exePath))
            {
                exeVersion = (await _services.Detector.ResolveFromExecutableAsync(exePath))?.Version;
            }
        }
        else if (!string.IsNullOrWhiteSpace(vm.Entry.InstallDir) && Directory.Exists(vm.Entry.InstallDir))
        {
            exeVersion = (await _services.Detector.ResolveExeOnlyFromInstallDirAsync(vm.Entry.InstallDir))?.Version;
        }

        string? softwareVersion = null;
        if (!string.IsNullOrWhiteSpace(exePath) && InstalledAppDetector.IsWindowsAppsAliasPath(exePath))
        {
            softwareVersion = (await _services.Detector.ResolveFromExecutableAsync(exePath))?.Version;
        }
        else
        {
            if (forceScan)
            {
                await _services.Detector.ScanInstalledAsync(forceRefresh: true);
            }

            var candidates = await _services.Detector.FindCandidatesAsync(vm.Entry);
            softwareVersion = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Version) && c.Score >= 30)?.Version;
            if (string.IsNullOrWhiteSpace(softwareVersion))
            {
                var targetDir = !string.IsNullOrWhiteSpace(vm.Entry.InstallDir)
                    ? vm.Entry.InstallDir
                    : (!string.IsNullOrWhiteSpace(exePath) ? Path.GetDirectoryName(exePath) : null);
                if (!string.IsNullOrWhiteSpace(targetDir) && Directory.Exists(targetDir))
                {
                    var normalizedTarget = NormalizeDirPath(targetDir);
                    var installed = await _services.Detector.ScanInstalledAsync(forceRefresh: false);
                    softwareVersion = installed
                        .Where(a => !string.IsNullOrWhiteSpace(a.Version)
                                    && NormalizeDirPath(a.InstallLocation) == normalizedTarget)
                        .OrderByDescending(a => a.Version, Comparer<string?>.Create(VersionComparer.Compare))
                        .FirstOrDefault()?.Version;
                }
            }
        }

        return (exeVersion, softwareVersion);
    }

    private void ReconcileResultWithDualVersions(
        EntryViewModel vm,
        CheckResult result,
        string? exeVersion,
        string? softwareVersion)
    {
        if (result.State == UpdateState.Failed
            || result.Update is null
            || string.IsNullOrWhiteSpace(result.Update.Version)
            || vm.IsNotInstalled)
        {
            return;
        }

        var reconciled = UpdateChecker.ReconcileVersionWithLatest(exeVersion, softwareVersion, result.Update.Version);
        if (!string.IsNullOrWhiteSpace(reconciled)
            && !string.Equals(vm.Entry.CurrentVersion, reconciled, StringComparison.OrdinalIgnoreCase))
        {
            vm.Entry.CurrentVersion = reconciled;
            UpdateEntryReference(vm);
            result.State = UpdateChecker.Evaluate(vm.Entry, result.Update);
        }
    }

    private static string NormalizeDirPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))
            .Replace('/', '\\')
            .TrimEnd('\\')
            .ToLowerInvariant();
    }

    /// <summary>
    /// 从条目绑定的主程序 exe 读取当前版本（「选 EXE」绑定的条目按用户要求永远以 exe 为准）。
    /// 返回读到的最新版本；没有绑定或读取失败返回 null。
    /// </summary>
    private async Task<string?> RefreshVersionFromBoundExeAsync(
        EntryViewModel vm,
        bool persistWhenChanged = true,
        bool clearWhenMissing = false)
    {
        var exePath = vm.Entry.ExePath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        var isAlias = InstalledAppDetector.IsWindowsAppsAliasPath(exePath);
        if (!isAlias && !File.Exists(exePath))
        {
            if (clearWhenMissing && !string.IsNullOrWhiteSpace(vm.Entry.CurrentVersion))
            {
                vm.Entry.CurrentVersion = string.Empty;
                UpdateEntryReference(vm);
                if (persistWhenChanged)
                {
                    Persist();
                    RefreshStats();
                }

                AppLog.Write($"绑定的主程序不存在（软件尚未安装），已清空当前版本：{vm.Name}（{exePath}）");
            }

            vm.RefreshFromEntry();
            vm.EvaluateState();
            return null;
        }

        try
        {
            var detected = await _services.Detector.ResolveFromExecutableAsync(exePath);
            var version = detected?.Version;
            if (string.IsNullOrWhiteSpace(version) && !isAlias)
            {
                var dir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    version = (await _services.Detector.ResolveFromInstallDirAsync(dir))?.Version;
                }
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                vm.RefreshFromEntry();
                vm.EvaluateState();
                return null;
            }

            if (!string.Equals(vm.Entry.CurrentVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                vm.Entry.CurrentVersion = version!;
                vm.Entry.LastError = null;
                vm.RefreshFromEntry();
                vm.EvaluateState();
                UpdateEntryReference(vm);
                if (persistWhenChanged)
                {
                    Persist();
                    RefreshStats();
                }

                AppLog.Write($"已按绑定 exe 刷新版本：{vm.Name} → {version}（{exePath}）");
            }

            return version;
        }
        catch (Exception ex)
        {
            AppLog.Write($"读取绑定 exe 版本失败：{exePath}", ex);
            return null;
        }
    }

    /// <summary>
    /// 安装结束后读实际安装版本（注册表 → 安装目录 exe 文件版本），并做严格校验后才回填：
    /// - 只取相关度最高的那个候选（不能取"所有候选里最大的版本"，否则会错配到别的软件）；
    /// - 必须比原版本新；
    /// - 且不能超过远端最新版本（超过说明匹配错了，例如把别的软件 158.x 读了进来）。
    /// 绑定过 exe 的条目：只认那个 exe，不走注册表匹配。
    /// </summary>
    private async Task<string?> TryRefreshInstalledVersionAsync(
        EntryViewModel vm,
        string oldVersion,
        string latestVersion)
    {
        try
        {
            // 统一走「当前版本」刷新逻辑（绑定 exe → 安装目录 → 注册表最高分候选）
            var detected = await RefreshCurrentVersionFromInstalledAsync(vm, forceScan: true);
            if (string.IsNullOrWhiteSpace(detected))
            {
                return null;
            }

            // 安装流程里只关心"版本有没有变化"：变了就回填并结束轮询
            if (string.Equals(detected, oldVersion, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // 非绑定 exe 的条目额外做一次合理性校验：读到的版本不应超过远端最新（超过说明匹配错了）
            if (string.IsNullOrWhiteSpace(vm.Entry.ExePath)
                && !string.IsNullOrWhiteSpace(latestVersion)
                && VersionComparer.IsNewer(latestVersion, detected))
            {
                AppLog.Write($"安装后读到的版本 {detected} 超过远端最新 {latestVersion}，判定为错误匹配，已忽略");
                return null;
            }

            return detected;
        }
        catch (Exception ex)
        {
            AppLog.Write("安装后自动读取版本失败", ex);
            return null;
        }
    }

    public async Task UpdateAllAsync()
    {
        var targets = Entries.Where(e => BelongsToCurrentListPage(e) && e.State == UpdateState.UpdateAvailable).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var ok = Confirm?.Invoke("批量更新", $"将依次下载并启动 {targets.Count} 个安装器。\n继续？") ?? false;
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var index = 0;
            foreach (var vm in targets)
            {
                index++;
                BusyText = $"批量更新 {index}/{targets.Count}：{vm.Name}";
                await UpdateOneAsync(vm);
            }

            StatusText = $"批量更新结束：处理了 {targets.Count} 个软件";
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    // ---------- 增删改 ----------

    public Task AddEntryAsync()
    {
        var defaultSource = _currentPage == MainPage.Official ? SourceKind.Custom : SourceKind.GitHub;
        var created = EditEntryRequested?.Invoke(null, defaultSource);
        if (created is null)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(created.Id))
        {
            created.Id = Guid.NewGuid().ToString("N");
        }

        var vm = new EntryViewModel(this, created);
        Entries.Add(vm);
        _services.Data.Entries.Add(created);
        Persist();
        RefreshStats();
        StatusText = created.Source == SourceKind.Custom
            ? $"已添加「{vm.Name}」到软件下载列表"
            : $"已添加「{vm.Name}」，点它卡片上的「检查」获取新版本";

        return Task.CompletedTask;
    }

    public void EditEntry(EntryViewModel vm)
    {
        var edited = EditEntryRequested?.Invoke(vm.Entry.Clone(), vm.Entry.Source);
        if (edited is null)
        {
            return;
        }

        var sourceChanged = edited.Source != vm.Entry.Source
                            || !string.Equals(edited.Package, vm.Entry.Package, StringComparison.Ordinal)
                            || edited.PackageIsRegex != vm.Entry.PackageIsRegex
                            || !string.Equals(edited.RepoUrl, vm.Entry.RepoUrl, StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(edited.HomepageUrl, vm.Entry.HomepageUrl, StringComparison.OrdinalIgnoreCase);

        vm.Entry.CopyEditableFrom(edited);
        if (sourceChanged)
        {
            vm.Entry.LatestVersion = null;
            vm.Entry.MatchedAssetName = null;
            vm.Entry.DownloadUrl = null;
            vm.Entry.ReleasePageUrl = null;
            vm.Entry.LastError = null;
        }

        vm.RefreshFromEntry();
        vm.EvaluateState();
        UpdateEntryReference(vm);
        Persist();
        RefreshStats();
        StatusText = $"已保存「{vm.Name}」的修改";
    }

    public void DeleteEntry(EntryViewModel vm)
    {
        var ok = Confirm?.Invoke("删除条目", $"确定删除「{vm.Name}」吗？\n（只从清单移除，不会卸载软件）") ?? false;
        if (!ok)
        {
            return;
        }

        Entries.Remove(vm);
        _services.Data.Entries.RemoveAll(e => e.Id == vm.Entry.Id);
        Persist();
        RefreshStats();
        BackupPage.RefreshSummaryTexts();
        RaisePropertyChanged(nameof(IsEmpty));
        (CheckAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        StatusText = $"已删除「{vm.Name}」";
    }

    // ---------- 其他操作 ----------

    public void OpenInstallDir(EntryViewModel vm)
    {
        var dir = vm.Entry.InstallDir;
        if (string.IsNullOrWhiteSpace(dir))
        {
            ShowMessage?.Invoke("没有安装目录", $"「{vm.Name}」还没有设置安装目录。\n可以点「编辑」→「自动识别」补上。");
            return;
        }

        if (!Directory.Exists(dir))
        {
            ShowMessage?.Invoke("目录不存在", $"目录不存在：\n{dir}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write("打开安装目录失败", ex);
            ShowMessage?.Invoke("打开失败", ex.Message);
        }
    }

    /// <summary>
    /// 解析「下载页 / 发布页」地址：WinGet 优先官网（HomepageUrl）；GitHub 用 Releases；再兜底清单页。
    /// </summary>
    private static string? ResolvePageUrl(EntryViewModel vm)
    {
        var url = !string.IsNullOrWhiteSpace(vm.Entry.HomepageUrl)
            ? vm.Entry.HomepageUrl
            : vm.Entry.ReleasePageUrl;

        if (string.IsNullOrWhiteSpace(url) && vm.Entry.Source == SourceKind.GitHub)
        {
            var parsed = AssetMatcher.ParseGitHubRepo(vm.Entry.RepoUrl);
            if (parsed is not null)
            {
                url = $"https://github.com/{parsed.Value.Owner}/{parsed.Value.Repo}/releases";
            }
        }

        if (string.IsNullOrWhiteSpace(url) && vm.Entry.Source == SourceKind.WinGet
            && !string.IsNullOrWhiteSpace(vm.Entry.Package))
        {
            var segments = vm.Entry.Package.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length > 0)
            {
                var path = "manifests/" + char.ToLowerInvariant(segments[0][0]) + "/" + string.Join("/", segments);
                url = "https://github.com/microsoft/winget-pkgs/tree/master/" + path;
            }
        }

        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    /// <summary>静默打开下载页（更新失败后自动调用）。</summary>
    private void OpenDownloadPageQuietly(EntryViewModel vm)
    {
        var url = ResolvePageUrl(vm);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            AppLog.Write($"更新失败后已自动打开下载页：{url}");
        }
        catch (Exception ex)
        {
            AppLog.Write("自动打开下载页失败", ex);
        }
    }

    public void OpenReleasePage(EntryViewModel vm)
    {
        // WinGet 条目：优先打开软件官网（清单链接过期时可直接手动下载）；GitHub 用 Releases
        var url = ResolvePageUrl(vm);

        // WinGet 来源：直接打开该包在 winget-pkgs 里的清单目录（没检查过也能打开）
        if (string.IsNullOrWhiteSpace(url) && vm.Entry.Source == SourceKind.WinGet
            && !string.IsNullOrWhiteSpace(vm.Entry.Package))
        {
            var id = vm.Entry.Package.Trim();
            var segments = id.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length > 0)
            {
                var path = "manifests/" + char.ToLowerInvariant(segments[0][0]) + "/" + string.Join("/", segments);
                url = "https://github.com/microsoft/winget-pkgs/tree/master/" + path;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            ShowMessage?.Invoke("没有发布页", "还没有检查过该软件，或来源不支持发布页。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write("打开发布页失败", ex);
            ShowMessage?.Invoke("打开失败", ex.Message);
        }
    }

    public void CopyInfo(EntryViewModel vm)
    {
        var e = vm.Entry;
        var text = string.Join(Environment.NewLine,
            $"项目名称：{e.Name}",
            $"包：{e.Package}",
            $"版本：{e.CurrentVersion}",
            $"新版本：{e.LatestVersion}",
            $"安装目录：{e.InstallDir}",
            $"来源：{vm.SourceText}",
            e.Source == SourceKind.GitHub ? $"仓库：{e.RepoUrl}" : string.Empty);

        try
        {
            Clipboard.SetText(text.TrimEnd());
            StatusText = $"已复制「{e.Name}」的信息到剪贴板";
        }
        catch (Exception ex)
        {
            AppLog.Write("复制失败", ex);
        }
    }

    /// <summary>改选 GitHub Assets 里的安装包。</summary>
    public async Task ChooseAssetAsync(EntryViewModel vm)
    {
        if (vm.Entry.Source == SourceKind.Custom)
        {
            ShowMessage?.Invoke("官网/手动来源", $"「{vm.Name}」是官网/手动来源，没有可选的安装包；请直接点「更新」打开官网下载。");
            return;
        }

        try
        {
            IsBusy = true;
            BusyText = "正在获取候选安装包…";
            IUpdateSourceProvider provider = vm.Entry.Source == SourceKind.GitHub ? _services.GitHub : _services.Winget;
            AvailableUpdate update;
            try
            {
                update = await provider.GetLatestAsync(vm.Entry);
            }
            catch (UpdateSourceException) when (vm.Entry.Source == SourceKind.GitHub && !string.IsNullOrWhiteSpace(vm.Entry.Package))
            {
                var fallbackEntry = vm.Entry.Clone();
                fallbackEntry.Package = string.Empty;
                fallbackEntry.PackageIsRegex = false;
                update = await provider.GetLatestAsync(fallbackEntry);
            }

            if (update.Candidates.Count == 0)
            {
                ShowMessage?.Invoke("没有候选安装包", "没有解析到任何候选安装包。");
                return;
            }

            var picked = ChooseAssetRequested?.Invoke(update);
            if (picked is null)
            {
                return;
            }

            vm.Entry.MatchedAssetName = picked.Name;
            vm.Entry.DownloadUrl = picked.Url;
            vm.Entry.LatestVersion = update.Version;
            vm.Entry.ReleasePageUrl = update.ReleasePageUrl;
            vm.Entry.LastError = null;
            vm.Entry.LastCheckedAt = DateTimeOffset.Now;
            vm.RefreshFromEntry();
            vm.EvaluateState();
            UpdateEntryReference(vm);
            Persist();
            RefreshStats();
            StatusText = $"已改用安装包：{picked.Name}";
        }
        catch (Exception ex)
        {
            AppLog.Write("改选安装包失败", ex);
            ShowMessage?.Invoke("获取候选包失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private void SelectFilter(object? parameter)
    {
        if (parameter is not FilterChip chip)
        {
            return;
        }

        _filter = chip.Value;
        foreach (var item in Filters)
        {
            item.IsSelected = item.Value == _filter;
        }

        EntriesView.Refresh();
    }

    /// <summary>来源筛选（全部 / GitHub / WinGet）。</summary>
    private void SelectSource(object? parameter)
    {
        if (parameter is not SourceFilterChip chip)
        {
            return;
        }

        _sourceFilter = chip.Value;
        foreach (var item in SourceFilters)
        {
            item.IsSelected = item.Value == _sourceFilter;
        }

        EntriesView.Refresh();
    }

    /// <summary>按指定列与方向排序软件列表（支持项目名称、状态、来源；direction=null 恢复默认顺序）。</summary>
    public void ApplySort(string? sortMember, ListSortDirection? direction)
    {
        if (EntriesView is ListCollectionView lcv)
        {
            lcv.CustomSort = string.IsNullOrWhiteSpace(sortMember) || direction is null
                ? null
                : new EntrySortComparer(sortMember, direction.Value);
            return;
        }

        EntriesView.SortDescriptions.Clear();
        if (!string.IsNullOrWhiteSpace(sortMember) && direction is not null)
        {
            EntriesView.SortDescriptions.Add(new SortDescription(sortMember, direction.Value));
        }
    }

    private sealed class EntrySortComparer : System.Collections.IComparer
    {
        private readonly string _member;
        private readonly int _sign;

        public EntrySortComparer(string member, ListSortDirection direction)
        {
            _member = member;
            _sign = direction == ListSortDirection.Ascending ? 1 : -1;
        }

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is not EntryViewModel a || y is not EntryViewModel b)
            {
                return 0;
            }

            var cmp = _member switch
            {
                nameof(EntryViewModel.Name) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name),
                nameof(EntryViewModel.SourceText) => StringComparer.CurrentCultureIgnoreCase.Compare(a.SourceText, b.SourceText),
                nameof(EntryViewModel.StateSortOrder) => a.StateSortOrder.CompareTo(b.StateSortOrder),
                _ => 0,
            };

            if (cmp != 0)
            {
                return cmp * _sign;
            }

            // 同来源/同状态时按项目名称升序保持稳定整齐
            return StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        }
    }

    // ---------- 内部 ----------

    private bool FilterPredicate(object obj)
    {
        if (obj is not EntryViewModel vm)
        {
            return false;
        }

        if (!BelongsToCurrentListPage(vm))
        {
            return false;
        }

        var stateOk = _filter switch
        {
            AppFilter.All => true,
            AppFilter.Updatable => vm.State == UpdateState.UpdateAvailable,
            AppFilter.Latest => vm.State == UpdateState.UpToDate,
            AppFilter.Failed => vm.State == UpdateState.Failed,
            AppFilter.Unknown => vm.State == UpdateState.Unknown,
            _ => true,
        };

        if (!stateOk)
        {
            return false;
        }

        if (ShowSourceFilters && _sourceFilter is not null && vm.Entry.Source != _sourceFilter.Value)
        {
            return false;
        }

        var search = _searchText.Trim().ToLowerInvariant();
        return search.Length == 0 || vm.SearchBlob.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshStats()
    {
        var pageEntries = Entries.Where(BelongsToCurrentListPage).ToList();

        TotalCount = pageEntries.Count;
        UpdatableCount = pageEntries.Count(e => e.State == UpdateState.UpdateAvailable);
        LatestCount = pageEntries.Count(e => e.State == UpdateState.UpToDate);
        FailedCount = pageEntries.Count(e => e.State == UpdateState.Failed);
        UnknownCount = pageEntries.Count(e => e.State == UpdateState.Unknown);
        InstalledCount = pageEntries.Count(e => !e.IsNotInstalled);
        NotInstalledCount = pageEntries.Count(e => e.IsNotInstalled);

        Filters[0].Count = TotalCount;
        Filters[1].Count = UpdatableCount;
        Filters[2].Count = LatestCount;
        Filters[3].Count = FailedCount;
        Filters[4].Count = UnknownCount;

        var last = pageEntries.Select(e => e.Entry.LastCheckedAt).Where(t => t is not null).Max();
        LastCheckText = last is null ? "尚未检查" : "上次检查 " + last.Value.LocalDateTime.ToString("MM-dd HH:mm");

        RaisePropertyChanged(nameof(IsEmpty));
        (CheckAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        EntriesView.Refresh();
    }

    private void Persist()
    {
        try
        {
            _services.SaveData();
        }
        catch (Exception ex)
        {
            AppLog.Write("保存清单失败", ex);
        }
    }

    /// <summary>把界面上的快照同步回数据集合里的对象（EntryViewModel.Entry 与 AppData.Entries 中的引用可能已不一致）。</summary>
    private void UpdateEntryReference(EntryViewModel vm)
    {
        var index = _services.Data.Entries.FindIndex(e => e.Id == vm.Entry.Id);
        if (index < 0)
        {
            _services.Data.Entries.Add(vm.Entry);
            return;
        }

        _services.Data.Entries[index] = vm.Entry;
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}
