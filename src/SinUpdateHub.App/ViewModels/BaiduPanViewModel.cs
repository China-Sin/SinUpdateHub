using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.ViewModels;

/// <summary>百度网盘面包屑路径段。</summary>
public sealed record BaiduBreadcrumbItem(string Label, string FullPath, bool IsLast);

/// <summary>百度网盘列表项 ViewModel。</summary>
public sealed class BaiduFileItemViewModel : ObservableObject
{
    public const string FolderPathData = "M10,4H4C2.9,4,2,4.9,2,6v12c0,1.1,0.9,2,2,2h16c1.1,0,2-0.9,2-2V8c0-1.1-0.9-2-2-2h-8L10,4z";
    private const string ArchivePathData = "M14,2H6C4.9,2,4,2.9,4,4v16c0,1.1,0.9,2,2,2h12c1.1,0,2-0.9,2-2V8L14,2z M13,9V3.5L18.5,9H13z M9,6h2v2H9V6z M11,8h2v2h-2V8z M9,10h2v2H9V10z M11,12h2v2h-2V12z M9,14h4v3H9V14z";
    private const string ExecutablePathData = "M4,4h16c1.1,0,2,0.9,2,2v12c0,1.1-0.9,2-2,2H4c-1.1,0-2-0.9-2-2V6C2,4.9,2.9,4,4,4z M4,8v10h16V8H4z M7,10.5l3.5,2.5L7,15.5V10.5z M12,14h5v1.5h-5V14z";
    private const string DiscPathData = "M12,2C6.48,2,2,6.48,2,12s4.48,10,10,10s10-4.48,10-10S17.52,2,12,2z M12,15c-1.66,0-3-1.34-3-3s1.34-3,3-3s3,1.34,3,3S13.66,15,12,15z M12,11c-0.55,0-1,0.45-1,1s0.45,1,1,1s1-0.45,1-1S12.55,11,12,11z";
    private const string GenericFilePathData = "M14,2H6C4.9,2,4,2.9,4,4v16c0,1.1,0.9,2,2,2h12c1.1,0,2-0.9,2-2V8L14,2z M13,9V3.5L18.5,9H13z M8,13h8v1.5H8V13z M8,16h6v1.5H8V16z";

    private bool _isSelected;

    public BaiduFileItemViewModel(BaiduFileEntry entry, bool showParentPath = false)
    {
        Entry = entry;
        ShowParentPath = showParentPath;
    }

    public BaiduFileEntry Entry { get; }

    public bool ShowParentPath { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsDir => Entry.IsDir;

    public string Name => Entry.Name;

    public string Path => Entry.Path;

    public string ParentDirText => BaiduPanService.GetParentDir(Entry.Path);

    public string IconPathData
    {
        get
        {
            if (Entry.IsDir) return FolderPathData;
            var ext = System.IO.Path.GetExtension(Entry.Name).ToLowerInvariant();
            return ext switch
            {
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".xz" => ArchivePathData,
                ".exe" or ".msi" or ".msix" or ".msixbundle" or ".bat" or ".cmd" or ".ps1" => ExecutablePathData,
                ".iso" or ".img" or ".wim" or ".vhd" or ".vhdx" => DiscPathData,
                _ => GenericFilePathData,
            };
        }
    }

    public string IconColorHex
    {
        get
        {
            if (Entry.IsDir) return "#E6B422";
            var ext = System.IO.Path.GetExtension(Entry.Name).ToLowerInvariant();
            return ext switch
            {
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".xz" => "#A371F7",
                ".exe" or ".msi" or ".msix" or ".msixbundle" or ".bat" or ".cmd" or ".ps1" => "#3FB950",
                ".iso" or ".img" or ".wim" or ".vhd" or ".vhdx" => "#58A6FF",
                _ => "#8B949E",
            };
        }
    }

    public string TypeText => Entry.IsDir ? "文件夹" : "文件";

    public string SizeText => Entry.IsDir ? "—" : FormatSize(Entry.Size);

    public string ModifiedText => Entry.ModifiedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return string.Empty;
        if (bytesPerSecond < 1024) return $"{bytesPerSecond:F0} B/s";
        if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024.0:F1} KB/s";
        return $"{bytesPerSecond / (1024.0 * 1024):F1} MB/s";
    }

    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is null || eta.Value <= TimeSpan.Zero) return string.Empty;
        var t = eta.Value;
        return t.TotalHours >= 1
            ? $"剩余 {(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"剩余 {t.Minutes:D2}:{t.Seconds:D2}";
    }
}

/// <summary>百度网盘页面 ViewModel：手动配置 AppKey/SecretKey、内置 OAuth2 登录、目录浏览、全网盘搜索、断点续传下载与分片上传。</summary>
public sealed class BaiduPanViewModel : ObservableObject
{
    private readonly AppServices _services;

    private string _appKey;
    private bool _showAppKey;
    private string _secretKey;
    private bool _showSecretKey;
    private string _currentDir;
    private string _localDownloadDir;
    private string _searchKeyword = string.Empty;
    private bool _isSearchMode;
    private bool _isBusy;
    private bool _isTransferring;
    private double _downloadProgressPercent;
    private string _downloadProgressText = string.Empty;
    private string _statusMessage = "请先填写 AppKey 和 SecretKey，然后点击「登录百度网盘」";
    private CancellationTokenSource? _transferCts;

    public BaiduPanViewModel(AppServices services)
    {
        _services = services;
        var s = services.Settings;
        _appKey = s.BaiduAppKey;
        _secretKey = s.BaiduSecretKey;
        _currentDir = BaiduPanService.NormalizeRemoteDir(s.BaiduDefaultDir);
        _localDownloadDir = string.IsNullOrWhiteSpace(s.BaiduDownloadDir)
            ? AppPaths.DownloadDir(s.DownloadDir)
            : s.BaiduDownloadDir;

        if (IsLoggedIn)
        {
            _statusMessage = $"已登录：{AccountStatusText}，支持断点续传下载、全网盘搜索与文件上传";
        }

        SaveConfigCommand = new RelayCommand(SaveConfig);
        OpenConsoleCommand = new RelayCommand(OpenConsole);
        LoginCommand = new AsyncRelayCommand(LoginAsync, () => !IsBusy && !IsTransferring);
        LogoutCommand = new RelayCommand(Logout, () => IsLoggedIn && !IsBusy && !IsTransferring);
        NavigateDirCommand = new AsyncRelayCommand(RefreshDirAsync, () => IsLoggedIn && !IsBusy && !IsTransferring);
        NavigateBreadcrumbCommand = new AsyncRelayCommand(NavigateBreadcrumbAsync, _ => IsLoggedIn && !IsBusy && !IsTransferring);
        GoParentCommand = new AsyncRelayCommand(GoParentAsync, () => IsLoggedIn && !IsBusy && !IsTransferring && (IsSearchMode || CurrentDir != "/"));
        RefreshCommand = new AsyncRelayCommand(RefreshOrRepeatSearchAsync, () => IsLoggedIn && !IsBusy && !IsTransferring);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => IsLoggedIn && !IsBusy && !IsTransferring);
        ClearSearchCommand = new AsyncRelayCommand(ClearSearchAsync, () => IsLoggedIn && !IsBusy && !IsTransferring);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true), () => Items.Count > 0 && !IsTransferring);
        ClearSelectionCommand = new RelayCommand(() => SetAllSelected(false), () => Items.Count > 0 && !IsTransferring);
        ChooseDownloadDirCommand = new RelayCommand(ChooseDownloadDir, () => !IsTransferring);
        OpenDownloadDirCommand = new RelayCommand(OpenDownloadDir);
        DownloadSelectedCommand = new AsyncRelayCommand(DownloadSelectedAsync, () => IsLoggedIn && !IsBusy && !IsTransferring && Items.Count > 0);
        DownloadCurrentFolderCommand = new AsyncRelayCommand(DownloadCurrentFolderAsync, () => IsLoggedIn && !IsBusy && !IsTransferring && Items.Count > 0);
        UploadFilesCommand = new AsyncRelayCommand(UploadFilesAsync, () => IsLoggedIn && !IsBusy && !IsTransferring);
        CancelDownloadCommand = new RelayCommand(CancelTransfer, () => IsTransferring);

        RebuildBreadcrumbs();
    }

    /// <summary>请求主窗口弹出百度 OAuth2 登录窗口，返回授权码 code（取消返回 null）。</summary>
    public Func<string, string?>? AuthorizeBaiduRequested { get; set; }

    /// <summary>请求主窗口弹出本地目录选择器，返回选定路径（取消返回 null）。</summary>
    public Func<string, string?>? PickFolderRequested { get; set; }

    /// <summary>请求主窗口弹出多文件选择器，返回待上传的本地路径数组（取消返回 null）。</summary>
    public Func<string[]?>? PickFilesRequested { get; set; }

    public Action<string, string>? ShowMessage { get; set; }

    public Func<string, string, bool>? Confirm { get; set; }

    public ObservableCollection<BaiduFileItemViewModel> Items { get; } = new();

    public ObservableCollection<BaiduBreadcrumbItem> Breadcrumbs { get; } = new();

    public string AppKey
    {
        get => _appKey;
        set => SetProperty(ref _appKey, value);
    }

    public bool ShowAppKey
    {
        get => _showAppKey;
        set => SetProperty(ref _showAppKey, value);
    }

    public string SecretKey
    {
        get => _secretKey;
        set => SetProperty(ref _secretKey, value);
    }

    public bool ShowSecretKey
    {
        get => _showSecretKey;
        set => SetProperty(ref _showSecretKey, value);
    }

    public bool IsLoggedIn => !string.IsNullOrWhiteSpace(_services.Settings.BaiduAccessToken);

    public string AccountStatusText
    {
        get
        {
            if (!IsLoggedIn)
            {
                return "未登录";
            }

            var name = string.IsNullOrWhiteSpace(_services.Settings.BaiduUserName)
                ? "已授权账号"
                : _services.Settings.BaiduUserName;
            var vip = _services.Settings.BaiduVipType switch
            {
                2 => "SVIP",
                1 => "VIP",
                _ => "普通用户",
            };
            return $"{name}（{vip}）";
        }
    }

    public string CurrentDir
    {
        get => _currentDir;
        set
        {
            if (SetProperty(ref _currentDir, value))
            {
                RebuildBreadcrumbs();
                RaiseCommands();
            }
        }
    }

    public string LocalDownloadDir
    {
        get => _localDownloadDir;
        set => SetProperty(ref _localDownloadDir, value);
    }

    public string SearchKeyword
    {
        get => _searchKeyword;
        set => SetProperty(ref _searchKeyword, value);
    }

    public bool IsSearchMode
    {
        get => _isSearchMode;
        private set
        {
            if (SetProperty(ref _isSearchMode, value))
            {
                RaisePropertyChanged(nameof(ItemCountText));
                RaiseCommands();
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
                RaiseCommands();
            }
        }
    }

    public bool IsTransferring
    {
        get => _isTransferring;
        private set
        {
            if (SetProperty(ref _isTransferring, value))
            {
                OnPropertyChanged(nameof(IsDownloading));
                RaiseCommands();
            }
        }
    }

    /// <summary>兼容已有 XAML 绑定：传输（下载/上传）进行中。</summary>
    public bool IsDownloading => IsTransferring;

    public double DownloadProgressPercent
    {
        get => _downloadProgressPercent;
        private set => SetProperty(ref _downloadProgressPercent, value);
    }

    public string DownloadProgressText
    {
        get => _downloadProgressText;
        private set => SetProperty(ref _downloadProgressText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasNoItems => Items.Count == 0;

    public string ItemCountText
    {
        get
        {
            if (Items.Count == 0)
            {
                return IsSearchMode ? "未搜索到匹配的网盘文件" : "当前目录暂无文件";
            }

            var prefix = IsSearchMode ? $"搜索结果 {Items.Count} 项" : $"共 {Items.Count} 项";
            return $"{prefix}（{Items.Count(i => i.IsDir)} 个文件夹 · {Items.Count(i => !i.IsDir)} 个文件）";
        }
    }

    // ---------- 命令 ----------

    public ICommand SaveConfigCommand { get; }
    public ICommand OpenConsoleCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand NavigateDirCommand { get; }
    public ICommand NavigateBreadcrumbCommand { get; }
    public ICommand GoParentCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand ChooseDownloadDirCommand { get; }
    public ICommand OpenDownloadDirCommand { get; }
    public ICommand DownloadSelectedCommand { get; }
    public ICommand DownloadCurrentFolderCommand { get; }
    public ICommand UploadFilesCommand { get; }
    public ICommand CancelDownloadCommand { get; }

    // ---------- 账号与配置 ----------

    public void SaveConfig()
    {
        var s = _services.Settings;
        s.BaiduAppKey = (AppKey ?? string.Empty).Trim();
        s.BaiduSecretKey = (SecretKey ?? string.Empty).Trim();
        s.BaiduDefaultDir = BaiduPanService.NormalizeRemoteDir(CurrentDir);
        s.BaiduDownloadDir = (LocalDownloadDir ?? string.Empty).Trim();
        _services.SaveSettings();
        CurrentDir = s.BaiduDefaultDir;
        StatusMessage = "百度网盘配置已加密保存";
    }

    private void OpenConsole()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://pan.baidu.com/union/console/applist",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Write("打开百度网盘开放平台失败", ex);
        }
    }

    private async Task LoginAsync()
    {
        SaveConfig();
        var key = _services.Settings.BaiduAppKey;
        var secret = _services.Settings.BaiduSecretKey;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
        {
            ShowMessage?.Invoke("缺少密钥", "请先填写你在百度网盘开放平台创建的 AppKey 和 SecretKey。");
            return;
        }

        var code = AuthorizeBaiduRequested?.Invoke(key);
        if (string.IsNullOrWhiteSpace(code))
        {
            StatusMessage = "已取消百度网盘登录";
            return;
        }

        IsBusy = true;
        StatusMessage = "正在换取百度网盘访问令牌…";
        try
        {
            var token = await _services.BaiduPan.ExchangeCodeAsync(key, secret, code);
            var s = _services.Settings;
            s.BaiduAccessToken = token.AccessToken;
            s.BaiduRefreshToken = token.RefreshToken;
            s.BaiduTokenExpiresAt = token.ExpiresAt;

            try
            {
                var uinfo = await _services.BaiduPan.GetUserInfoAsync(s, () => _services.SaveSettings());
                s.BaiduUserName = uinfo.DisplayName;
                s.BaiduVipType = uinfo.VipType;
            }
            catch (Exception ex)
            {
                AppLog.Write("读取百度网盘用户信息失败（不影响使用）", ex);
            }

            _services.SaveSettings();
            RaisePropertyChanged(nameof(IsLoggedIn), nameof(AccountStatusText));
            RaiseCommands();
            StatusMessage = $"登录成功：{AccountStatusText}，正在读取目录…";
        }
        catch (Exception ex)
        {
            AppLog.Write("百度网盘登录失败", ex);
            StatusMessage = "登录失败：" + ex.Message;
            ShowMessage?.Invoke("登录失败", ex.Message);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshDirAsync();
    }

    /// <summary>切到百度网盘页时若已登录且尚未加载过列表，则自动加载默认目录。</summary>
    public async Task OnShownAsync()
    {
        if (!IsLoggedIn || IsBusy || IsTransferring)
        {
            return;
        }

        if (Items.Count == 0 && !IsSearchMode)
        {
            await RefreshDirAsync();
        }
    }

    private void Logout()
    {
        if (!(Confirm?.Invoke("退出登录", "确定退出当前百度网盘账号并清除本地保存的令牌吗？（AppKey 与 SecretKey 会保留）") ?? false))
        {
            return;
        }

        var s = _services.Settings;
        s.BaiduAccessToken = string.Empty;
        s.BaiduRefreshToken = string.Empty;
        s.BaiduTokenExpiresAt = null;
        s.BaiduUserName = string.Empty;
        s.BaiduVipType = 0;
        _services.SaveSettings();

        IsSearchMode = false;
        SearchKeyword = string.Empty;
        Items.Clear();
        RaisePropertyChanged(nameof(IsLoggedIn), nameof(AccountStatusText), nameof(HasNoItems), nameof(ItemCountText));
        RaiseCommands();
        StatusMessage = "已退出百度网盘登录";
    }

    // ---------- 目录浏览与搜索 ----------

    private async Task NavigateBreadcrumbAsync(object? parameter)
    {
        if (parameter is string targetPath && !string.IsNullOrWhiteSpace(targetPath))
        {
            CurrentDir = targetPath;
            await RefreshDirAsync();
        }
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        var normalized = BaiduPanService.NormalizeRemoteDir(_currentDir);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            Breadcrumbs.Add(new BaiduBreadcrumbItem("根目录 /", "/", true));
            return;
        }

        Breadcrumbs.Add(new BaiduBreadcrumbItem("根目录", "/", false));
        var acc = string.Empty;
        for (var i = 0; i < parts.Length; i++)
        {
            acc += "/" + parts[i];
            Breadcrumbs.Add(new BaiduBreadcrumbItem(parts[i], acc, i == parts.Length - 1));
        }
    }

    public async Task OpenFolderAsync(BaiduFileItemViewModel? item)
    {
        if (item is null || !item.IsDir || IsBusy || IsTransferring)
        {
            return;
        }

        CurrentDir = item.Path;
        await RefreshDirAsync();
    }

    private async Task GoParentAsync()
    {
        if (IsSearchMode)
        {
            await ClearSearchAsync();
            return;
        }

        CurrentDir = BaiduPanService.GetParentDir(CurrentDir);
        await RefreshDirAsync();
    }

    private async Task RefreshOrRepeatSearchAsync()
    {
        if (IsSearchMode && !string.IsNullOrWhiteSpace(SearchKeyword))
        {
            await SearchAsync();
        }
        else
        {
            await RefreshDirAsync();
        }
    }

    public async Task RefreshDirAsync()
    {
        if (!IsLoggedIn || IsBusy || IsTransferring)
        {
            return;
        }

        IsSearchMode = false;
        CurrentDir = BaiduPanService.NormalizeRemoteDir(CurrentDir);
        _services.Settings.BaiduDefaultDir = CurrentDir;
        _services.SaveSettings();

        IsBusy = true;
        StatusMessage = $"正在读取目录 {CurrentDir} …";
        try
        {
            var entries = await _services.BaiduPan.ListFilesAsync(
                _services.Settings,
                CurrentDir,
                () => _services.SaveSettings());

            Items.Clear();
            foreach (var entry in entries)
            {
                Items.Add(new BaiduFileItemViewModel(entry, showParentPath: false));
            }

            RaisePropertyChanged(nameof(HasNoItems), nameof(ItemCountText));
            RaiseCommands();
            StatusMessage = $"已加载 {CurrentDir}（{ItemCountText}）";
        }
        catch (Exception ex)
        {
            AppLog.Write($"读取百度网盘目录失败：{CurrentDir}", ex);
            StatusMessage = "读取失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SearchAsync()
    {
        if (!IsLoggedIn || IsBusy || IsTransferring)
        {
            return;
        }

        var key = (SearchKeyword ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            await RefreshDirAsync();
            return;
        }

        IsBusy = true;
        StatusMessage = $"正在全网盘搜索「{key}」…";
        try
        {
            var entries = await _services.BaiduPan.SearchFilesAsync(
                _services.Settings,
                key,
                "/",
                () => _services.SaveSettings());

            IsSearchMode = true;
            Items.Clear();
            foreach (var entry in entries)
            {
                Items.Add(new BaiduFileItemViewModel(entry, showParentPath: true));
            }

            RaisePropertyChanged(nameof(HasNoItems), nameof(ItemCountText));
            RaiseCommands();
            StatusMessage = $"全网盘搜索「{key}」完成：{ItemCountText}";
        }
        catch (Exception ex)
        {
            AppLog.Write($"搜索百度网盘失败：{key}", ex);
            StatusMessage = "搜索失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearSearchAsync()
    {
        SearchKeyword = string.Empty;
        IsSearchMode = false;
        await RefreshDirAsync();
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var item in Items)
        {
            item.IsSelected = selected;
        }
    }

    private void ChooseDownloadDir()
    {
        var initial = ResolveLocalDownloadDir();
        var picked = PickFolderRequested?.Invoke(initial);
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        LocalDownloadDir = picked.Trim();
        _services.Settings.BaiduDownloadDir = LocalDownloadDir;
        _services.SaveSettings();
        StatusMessage = $"本地下载目录已设置为：{LocalDownloadDir}";
    }

    private void OpenDownloadDir()
    {
        try
        {
            var dir = ResolveLocalDownloadDir();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowMessage?.Invoke("打开目录失败", ex.Message);
        }
    }

    // ---------- 下载（支持断点续传、已存在跳过、实时网速/ETA） ----------

    private async Task DownloadSelectedAsync()
    {
        var selected = Items.Where(i => i.IsSelected).Select(i => i.Entry).ToList();
        if (selected.Count == 0)
        {
            ShowMessage?.Invoke("未选择项目", "请先勾选要下载的文件或文件夹，或直接点击「下载当前文件夹全部」。");
            return;
        }

        await DownloadEntriesAsync(selected);
    }

    private async Task DownloadCurrentFolderAsync()
    {
        if (Items.Count == 0)
        {
            return;
        }

        var scopeLabel = IsSearchMode ? $"当前搜索结果（{Items.Count} 项）" : $"目录「{CurrentDir}」（{Items.Count} 项，含全部子文件夹）";
        var ok = Confirm?.Invoke(
            "确认批量下载",
            $"确定下载 {scopeLabel} 到本地目录吗？\n保存到：{ResolveLocalDownloadDir()}\n\n提示：本地已存在同大小文件将自动跳过，未完成大文件支持断点续传。") ?? false;
        if (!ok)
        {
            return;
        }

        await DownloadEntriesAsync(Items.Select(i => i.Entry).ToList());
    }

    private async Task DownloadEntriesAsync(IReadOnlyList<BaiduFileEntry> roots)
    {
        var saveRoot = ResolveLocalDownloadDir();
        Directory.CreateDirectory(saveRoot);

        _transferCts?.Dispose();
        _transferCts = new CancellationTokenSource();
        var ct = _transferCts.Token;

        IsTransferring = true;
        DownloadProgressPercent = 0;
        DownloadProgressText = "正在扫描待下载文件列表…";
        StatusMessage = "正在扫描文件夹内容…";

        try
        {
            var baseDirForRelative = IsSearchMode ? "/" : CurrentDir;
            var tasks = await _services.BaiduPan.CollectFilesRecursiveAsync(
                _services.Settings,
                roots,
                baseDirForRelative,
                () => _services.SaveSettings(),
                ct);

            if (tasks.Count == 0)
            {
                StatusMessage = "所选文件夹下没有可下载的文件";
                DownloadProgressText = string.Empty;
                return;
            }

            DownloadProgressText = $"正在获取 {tasks.Count} 个文件的下载链接…";
            var dlinks = await _services.BaiduPan.GetDownloadLinksAsync(
                _services.Settings,
                tasks.Select(t => t.File.FsId).ToList(),
                () => _services.SaveSettings(),
                ct);

            var succeeded = 0;
            var skipped = 0;
            var failedList = new List<string>();

            for (var i = 0; i < tasks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = tasks[i];
                var fileIndex = i + 1;
                if (!dlinks.TryGetValue(item.File.FsId, out var dlink) || string.IsNullOrWhiteSpace(dlink))
                {
                    failedList.Add($"{item.File.Name}（未获取到下载链接）");
                    continue;
                }

                var targetPath = Path.Combine(saveRoot, item.RelativeLocalPath);
                var fileProgress = new Progress<BaiduTransferProgress>(p =>
                {
                    var overall = ((fileIndex - 1) + p.Fraction) / tasks.Count * 100.0;
                    DownloadProgressPercent = Math.Clamp(overall, 0, 100);
                    if (!string.IsNullOrWhiteSpace(p.Message))
                    {
                        DownloadProgressText = $"[{fileIndex}/{tasks.Count}] {item.File.Name} · {p.Message}";
                        return;
                    }

                    var sizeInfo = p.TotalBytes is > 0
                        ? $"{BaiduFileItemViewModel.FormatSize(p.TransferredBytes)} / {BaiduFileItemViewModel.FormatSize(p.TotalBytes.Value)}"
                        : BaiduFileItemViewModel.FormatSize(p.TransferredBytes);
                    var speed = BaiduFileItemViewModel.FormatSpeed(p.BytesPerSecond);
                    var eta = BaiduFileItemViewModel.FormatEta(p.EstimatedRemaining);
                    var resumeTag = p.IsResumed ? "续传 · " : string.Empty;
                    var tail = string.Join(" · ", new[] { sizeInfo, speed, eta }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    DownloadProgressText = $"[{fileIndex}/{tasks.Count}] {item.File.Name}  ({resumeTag}{p.Fraction:P0} · {tail})";
                });

                StatusMessage = $"正在下载 [{fileIndex}/{tasks.Count}] {item.File.Name} …";
                try
                {
                    var result = await _services.BaiduPan.DownloadFileAsync(
                        _services.Settings,
                        dlink,
                        targetPath,
                        item.File.Size,
                        fileProgress,
                        () => _services.SaveSettings(),
                        ct);
                    if (result.SkippedExisting)
                    {
                        skipped++;
                    }
                    else
                    {
                        succeeded++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Write($"下载百度网盘文件失败：{item.File.Path}", ex);
                    failedList.Add($"{item.File.Name}（{ex.Message}）");
                }
            }

            DownloadProgressPercent = 100;
            var skippedText = skipped > 0 ? $"，跳过已存在 {skipped} 个" : string.Empty;
            if (failedList.Count == 0)
            {
                DownloadProgressText = $"全部下载完成：新下载 {succeeded} 个{skippedText}";
                StatusMessage = $"下载完成：新下载 {succeeded} 个{skippedText}（目录：{saveRoot}）";
            }
            else
            {
                DownloadProgressText = $"下载结束：成功 {succeeded} 个{skippedText}，失败 {failedList.Count} 个";
                StatusMessage = DownloadProgressText;
                ShowMessage?.Invoke(
                    "部分文件下载失败",
                    $"成功 {succeeded} 个{skippedText}，失败 {failedList.Count} 个：\n" + string.Join("\n", failedList.Take(8)));
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消下载（未完成大文件已保留 .part 进度，下次点击下载可自动断点续传）";
            DownloadProgressText = "下载已取消（支持下次断点续传）";
        }
        catch (Exception ex)
        {
            AppLog.Write("百度网盘批量下载失败", ex);
            StatusMessage = "下载失败：" + ex.Message;
            DownloadProgressText = string.Empty;
            ShowMessage?.Invoke("下载失败", ex.Message);
        }
        finally
        {
            IsTransferring = false;
            _transferCts?.Dispose();
            _transferCts = null;
        }
    }

    // ---------- 上传本地文件到当前目录 ----------

    private async Task UploadFilesAsync()
    {
        var files = PickFilesRequested?.Invoke();
        if (files is null || files.Length == 0)
        {
            return;
        }

        var targetRemoteDir = BaiduPanService.NormalizeRemoteDir(CurrentDir);

        _transferCts?.Dispose();
        _transferCts = new CancellationTokenSource();
        var ct = _transferCts.Token;

        IsTransferring = true;
        DownloadProgressPercent = 0;
        DownloadProgressText = $"准备上传 {files.Length} 个文件到 {targetRemoteDir} …";
        StatusMessage = $"正在上传到 {targetRemoteDir} …";

        var uploadedCount = 0;
        var instantCount = 0;
        var failedList = new List<string>();

        try
        {
            for (var i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var localFile = files[i];
                var fileName = Path.GetFileName(localFile);
                var fileIndex = i + 1;

                var fileProgress = new Progress<BaiduTransferProgress>(p =>
                {
                    var overall = ((fileIndex - 1) + p.Fraction) / files.Length * 100.0;
                    DownloadProgressPercent = Math.Clamp(overall, 0, 100);
                    if (!string.IsNullOrWhiteSpace(p.Message))
                    {
                        DownloadProgressText = $"上传 [{fileIndex}/{files.Length}] {fileName} · {p.Message}";
                        return;
                    }

                    var sizeInfo = p.TotalBytes is > 0
                        ? $"{BaiduFileItemViewModel.FormatSize(p.TransferredBytes)} / {BaiduFileItemViewModel.FormatSize(p.TotalBytes.Value)}"
                        : BaiduFileItemViewModel.FormatSize(p.TransferredBytes);
                    var speed = BaiduFileItemViewModel.FormatSpeed(p.BytesPerSecond);
                    var eta = BaiduFileItemViewModel.FormatEta(p.EstimatedRemaining);
                    var tail = string.Join(" · ", new[] { sizeInfo, speed, eta }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    DownloadProgressText = $"上传 [{fileIndex}/{files.Length}] {fileName}  ({p.Fraction:P0} · {tail})";
                });

                StatusMessage = $"正在上传 [{fileIndex}/{files.Length}] {fileName} → {targetRemoteDir} …";
                try
                {
                    var res = await _services.BaiduPan.UploadFileAsync(
                        _services.Settings,
                        localFile,
                        targetRemoteDir,
                        fileProgress,
                        () => _services.SaveSettings(),
                        ct);
                    uploadedCount++;
                    if (res.InstantUploaded)
                    {
                        instantCount++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Write($"上传文件到百度网盘失败：{localFile}", ex);
                    failedList.Add($"{fileName}（{ex.Message}）");
                }
            }

            DownloadProgressPercent = 100;
            var instantNote = instantCount > 0 ? $"（含 {instantCount} 个秒传）" : string.Empty;
            if (failedList.Count == 0)
            {
                DownloadProgressText = $"上传完成：共 {uploadedCount} 个文件{instantNote}";
                StatusMessage = $"已上传 {uploadedCount} 个文件到 {targetRemoteDir}{instantNote}";
            }
            else
            {
                DownloadProgressText = $"上传结束：成功 {uploadedCount} 个，失败 {failedList.Count} 个";
                StatusMessage = DownloadProgressText;
                ShowMessage?.Invoke(
                    "部分文件上传失败",
                    $"成功 {uploadedCount} 个，失败 {failedList.Count} 个：\n" + string.Join("\n", failedList.Take(8)));
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消文件上传";
            DownloadProgressText = "上传已取消";
        }
        catch (Exception ex)
        {
            AppLog.Write("百度网盘上传失败", ex);
            StatusMessage = "上传失败：" + ex.Message;
            DownloadProgressText = string.Empty;
            ShowMessage?.Invoke("上传失败", ex.Message);
        }
        finally
        {
            IsTransferring = false;
            _transferCts?.Dispose();
            _transferCts = null;
        }

        if (uploadedCount > 0 && !IsSearchMode)
        {
            await RefreshDirAsync();
        }
    }

    public void CancelTransfer()
    {
        try
        {
            _transferCts?.Cancel();
        }
        catch
        {
            // ignore
        }
    }

    private string ResolveLocalDownloadDir()
    {
        if (!string.IsNullOrWhiteSpace(LocalDownloadDir))
        {
            return LocalDownloadDir.Trim();
        }

        return AppPaths.DownloadDir(_services.Settings.DownloadDir);
    }

    private void RaiseCommands()
    {
        (LoginCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (LogoutCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NavigateDirCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (NavigateBreadcrumbCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (GoParentCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SearchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClearSearchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SelectAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChooseDownloadDirCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DownloadSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DownloadCurrentFolderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UploadFilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelDownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
