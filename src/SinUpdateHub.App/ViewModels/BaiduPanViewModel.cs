using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.ViewModels;

/// <summary>百度网盘列表项 ViewModel。</summary>
public sealed class BaiduFileItemViewModel : ObservableObject
{
    private bool _isSelected;

    public BaiduFileItemViewModel(BaiduFileEntry entry)
    {
        Entry = entry;
    }

    public BaiduFileEntry Entry { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsDir => Entry.IsDir;

    public string Name => Entry.Name;

    public string Path => Entry.Path;

    public string Glyph => Entry.IsDir ? "\uE8B7" : "\uE8A5";

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
}

/// <summary>百度网盘页面 ViewModel：手动配置 AppKey/SecretKey、内置 OAuth2 登录、目录浏览与整文件夹/多选下载。</summary>
public sealed class BaiduPanViewModel : ObservableObject
{
    private readonly AppServices _services;

    private string _appKey;
    private string _secretKey;
    private bool _showSecretKey;
    private string _currentDir;
    private string _localDownloadDir;
    private bool _isBusy;
    private bool _isDownloading;
    private double _downloadProgressPercent;
    private string _downloadProgressText = string.Empty;
    private string _statusMessage = "请先填写 AppKey 和 SecretKey，然后点击「登录百度网盘」";
    private CancellationTokenSource? _downloadCts;

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
            _statusMessage = $"已登录：{AccountStatusText}，点击「刷新目录」即可浏览网盘文件";
        }

        SaveConfigCommand = new RelayCommand(SaveConfig);
        OpenConsoleCommand = new RelayCommand(OpenConsole);
        LoginCommand = new AsyncRelayCommand(LoginAsync, () => !IsBusy && !IsDownloading);
        LogoutCommand = new RelayCommand(Logout, () => IsLoggedIn && !IsBusy && !IsDownloading);
        NavigateDirCommand = new AsyncRelayCommand(RefreshDirAsync, () => IsLoggedIn && !IsBusy && !IsDownloading);
        GoParentCommand = new AsyncRelayCommand(GoParentAsync, () => IsLoggedIn && !IsBusy && !IsDownloading && CurrentDir != "/");
        RefreshCommand = new AsyncRelayCommand(RefreshDirAsync, () => IsLoggedIn && !IsBusy && !IsDownloading);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true), () => Items.Count > 0 && !IsDownloading);
        ClearSelectionCommand = new RelayCommand(() => SetAllSelected(false), () => Items.Count > 0 && !IsDownloading);
        ChooseDownloadDirCommand = new RelayCommand(ChooseDownloadDir, () => !IsDownloading);
        OpenDownloadDirCommand = new RelayCommand(OpenDownloadDir);
        DownloadSelectedCommand = new AsyncRelayCommand(DownloadSelectedAsync, () => IsLoggedIn && !IsBusy && !IsDownloading && Items.Count > 0);
        DownloadCurrentFolderCommand = new AsyncRelayCommand(DownloadCurrentFolderAsync, () => IsLoggedIn && !IsBusy && !IsDownloading && Items.Count > 0);
        CancelDownloadCommand = new RelayCommand(CancelDownload, () => IsDownloading);
    }

    /// <summary>请求主窗口弹出百度 OAuth2 登录窗口，返回授权码 code（取消返回 null）。</summary>
    public Func<string, string?>? AuthorizeBaiduRequested { get; set; }

    /// <summary>请求主窗口弹出本地目录选择器，返回选定路径（取消返回 null）。</summary>
    public Func<string, string?>? PickFolderRequested { get; set; }

    public Action<string, string>? ShowMessage { get; set; }

    public Func<string, string, bool>? Confirm { get; set; }

    public ObservableCollection<BaiduFileItemViewModel> Items { get; } = new();

    public string AppKey
    {
        get => _appKey;
        set => SetProperty(ref _appKey, value);
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
                RaiseCommands();
            }
        }
    }

    public string LocalDownloadDir
    {
        get => _localDownloadDir;
        set => SetProperty(ref _localDownloadDir, value);
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

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                RaiseCommands();
            }
        }
    }

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

    public string ItemCountText => Items.Count == 0
        ? "当前目录暂无文件"
        : $"共 {Items.Count} 项（{Items.Count(i => i.IsDir)} 个文件夹 · {Items.Count(i => !i.IsDir)} 个文件）";

    // ---------- 命令 ----------

    public ICommand SaveConfigCommand { get; }
    public ICommand OpenConsoleCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand NavigateDirCommand { get; }
    public ICommand GoParentCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand ChooseDownloadDirCommand { get; }
    public ICommand OpenDownloadDirCommand { get; }
    public ICommand DownloadSelectedCommand { get; }
    public ICommand DownloadCurrentFolderCommand { get; }
    public ICommand CancelDownloadCommand { get; }

    // ---------- 操作 ----------

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

        Items.Clear();
        RaisePropertyChanged(nameof(IsLoggedIn), nameof(AccountStatusText), nameof(HasNoItems), nameof(ItemCountText));
        RaiseCommands();
        StatusMessage = "已退出百度网盘登录";
    }

    public async Task OpenFolderAsync(BaiduFileItemViewModel? item)
    {
        if (item is null || !item.IsDir || IsBusy || IsDownloading)
        {
            return;
        }

        CurrentDir = item.Path;
        await RefreshDirAsync();
    }

    private async Task GoParentAsync()
    {
        CurrentDir = BaiduPanService.GetParentDir(CurrentDir);
        await RefreshDirAsync();
    }

    public async Task RefreshDirAsync()
    {
        if (!IsLoggedIn || IsBusy || IsDownloading)
        {
            return;
        }

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
                Items.Add(new BaiduFileItemViewModel(entry));
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

        await DownloadEntriesAsync(Items.Select(i => i.Entry).ToList());
    }

    private async Task DownloadEntriesAsync(IReadOnlyList<BaiduFileEntry> roots)
    {
        var saveRoot = ResolveLocalDownloadDir();
        Directory.CreateDirectory(saveRoot);

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        IsDownloading = true;
        DownloadProgressPercent = 0;
        DownloadProgressText = "正在扫描待下载文件列表…";
        StatusMessage = "正在扫描文件夹内容…";

        try
        {
            var tasks = await _services.BaiduPan.CollectFilesRecursiveAsync(
                _services.Settings,
                roots,
                CurrentDir,
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
                var fileProgress = new Progress<DownloadProgress>(p =>
                {
                    var overall = ((fileIndex - 1) + p.Fraction) / tasks.Count * 100.0;
                    DownloadProgressPercent = Math.Clamp(overall, 0, 100);
                    var sizeInfo = p.Total is > 0
                        ? $"{BaiduFileItemViewModel.FormatSize(p.Received)} / {BaiduFileItemViewModel.FormatSize(p.Total.Value)}"
                        : BaiduFileItemViewModel.FormatSize(p.Received);
                    DownloadProgressText = $"[{fileIndex}/{tasks.Count}] {item.File.Name}  ({p.Fraction:P0} · {sizeInfo})";
                });

                StatusMessage = $"正在下载 [{fileIndex}/{tasks.Count}] {item.File.Name} …";
                try
                {
                    await _services.BaiduPan.DownloadFileAsync(
                        _services.Settings,
                        dlink,
                        targetPath,
                        fileProgress,
                        () => _services.SaveSettings(),
                        ct);
                    succeeded++;
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
            if (failedList.Count == 0)
            {
                DownloadProgressText = $"全部下载完成：共 {succeeded} 个文件";
                StatusMessage = $"下载完成：已保存 {succeeded} 个文件到 {saveRoot}";
            }
            else
            {
                DownloadProgressText = $"下载结束：成功 {succeeded} 个，失败 {failedList.Count} 个";
                StatusMessage = DownloadProgressText;
                ShowMessage?.Invoke(
                    "部分文件下载失败",
                    $"成功 {succeeded} 个，失败 {failedList.Count} 个：\n" + string.Join("\n", failedList.Take(8)));
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消百度网盘下载";
            DownloadProgressText = "下载已取消";
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
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void CancelDownload()
    {
        try
        {
            _downloadCts?.Cancel();
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
        (GoParentCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SelectAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearSelectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChooseDownloadDirCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DownloadSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DownloadCurrentFolderCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelDownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
