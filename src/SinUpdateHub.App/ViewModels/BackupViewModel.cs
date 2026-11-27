using System.Collections.ObjectModel;
using System.Windows.Input;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.ViewModels;

/// <summary>备份与恢复窗口 ViewModel（WebDAV + 本地 JSON 导出导入）。</summary>
public sealed class BackupViewModel : ObservableObject
{
    private readonly AppServices _services;

    private string _webDavUrl;
    private string _webDavUser;
    private string _webDavPassword;
    private string _webDavFolder;
    private int _keepBackups;
    private bool _autoBackupEnabled;
    private int _autoBackupIntervalHours;
    private bool _isAutoBackingUp;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private BackupFileInfo? _selectedBackup;
    private bool _showPassword;

    public BackupViewModel(AppServices services)
    {
        _services = services;
        var s = services.Settings;
        _webDavUrl = string.IsNullOrWhiteSpace(s.WebDavUrl) ? "https://dav.jianguoyun.com/dav/" : s.WebDavUrl;
        _webDavUser = s.WebDavUser;
        _webDavPassword = s.WebDavPassword;
        _webDavFolder = string.IsNullOrWhiteSpace(s.WebDavFolder) ? "SinUpdateHub" : s.WebDavFolder;
        _keepBackups = s.KeepBackups;
        _autoBackupEnabled = s.AutoBackupEnabled;
        _autoBackupIntervalHours = Math.Clamp(s.AutoBackupIntervalHours, 1, 720);

        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsBusy);
        BackupNowCommand = new AsyncRelayCommand(BackupNowAsync, () => !IsBusy);
        RefreshListCommand = new AsyncRelayCommand(RefreshListAsync, () => !IsBusy);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !IsBusy && SelectedBackup is not null);
        DeleteBackupCommand = new AsyncRelayCommand(DeleteBackupAsync, () => !IsBusy && SelectedBackup is not null);
        ExportLocalCommand = new AsyncRelayCommand(ExportLocalAsync, () => !IsBusy);
        ImportLocalCommand = new AsyncRelayCommand(ImportLocalAsync, () => !IsBusy);
        OpenHelpCommand = new RelayCommand(OpenJianguoyunHelp);
    }

    /// <summary>恢复成功后通知主界面重新载入清单。</summary>
    public event Action? DataRestored;

    /// <summary>后台自动定时备份成功后通知主界面更新状态提示。</summary>
    public event Action<string>? AutoBackupCompleted;

    /// <summary>备份或导出前触发，用于先把内存/防抖缓存写入 _services.Data 并落盘。</summary>
    public Action? BeforeBackup { get; set; }

    public Action<string, string>? ShowMessage { get; set; }

    public Func<string, string, bool>? Confirm { get; set; }

    /// <summary>弹出选择性恢复窗口：(来源名称, 备份数据) -> 用户勾选结果，取消返回 null。</summary>
    public Func<string, AppData, RestoreSelectionResult?>? SelectRestoreItems { get; set; }

    // ---------- WebDAV 配置 ----------

    public string WebDavUrl
    {
        get => _webDavUrl;
        set => SetProperty(ref _webDavUrl, value);
    }

    public string WebDavUser
    {
        get => _webDavUser;
        set => SetProperty(ref _webDavUser, value);
    }

    public string WebDavPassword
    {
        get => _webDavPassword;
        set => SetProperty(ref _webDavPassword, value);
    }

    /// <summary>是否明文显示密码（方便核对粘贴内容是否完整）。</summary>
    public bool ShowPassword
    {
        get => _showPassword;
        set => SetProperty(ref _showPassword, value);
    }

    public string WebDavFolder
    {
        get => _webDavFolder;
        set => SetProperty(ref _webDavFolder, value);
    }

    public int KeepBackups
    {
        get => _keepBackups;
        set => SetProperty(ref _keepBackups, Math.Clamp(value, 0, 200));
    }

    public bool AutoBackupEnabled
    {
        get => _autoBackupEnabled;
        set
        {
            if (SetProperty(ref _autoBackupEnabled, value))
            {
                _services.Settings.AutoBackupEnabled = value;
                _services.SaveSettings();
                OnPropertyChanged(nameof(AutoBackupStatusText));
            }
        }
    }

    public int AutoBackupIntervalHours
    {
        get => _autoBackupIntervalHours;
        set
        {
            var clamped = Math.Clamp(value, 1, 720);
            if (SetProperty(ref _autoBackupIntervalHours, clamped))
            {
                _services.Settings.AutoBackupIntervalHours = clamped;
                _services.SaveSettings();
                OnPropertyChanged(nameof(AutoBackupStatusText));
            }
        }
    }

    public string AutoBackupStatusText
    {
        get
        {
            if (!_autoBackupEnabled)
            {
                return "自动定时备份已关闭";
            }

            if (!_services.Settings.IsWebDavConfigured)
            {
                return $"已开启（每 {_autoBackupIntervalHours} 小时），填好左侧 WebDAV 账号密码并保存后自动生效";
            }

            var last = _services.Settings.LastBackupAt;
            if (last is null)
            {
                return $"已开启（每 {_autoBackupIntervalHours} 小时），将在后台自动执行首次备份";
            }

            var next = last.Value.AddHours(_autoBackupIntervalHours);
            return next <= DateTimeOffset.Now
                ? $"已开启（每 {_autoBackupIntervalHours} 小时），即将执行下一次自动备份"
                : $"下次自动备份：{next.LocalDateTime:yyyy-MM-dd HH:mm}（每 {_autoBackupIntervalHours} 小时）";
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                (TestConnectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (BackupNowCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RefreshListCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (RestoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (DeleteBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ExportLocalCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (ImportLocalCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string LastBackupText
    {
        get
        {
            var last = _services.Settings.LastBackupAt;
            return last is null ? "还没有备份过" : "上次备份 " + last.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        }
    }

    public string EntryCountText
    {
        get
        {
            var appCount = _services.Data.Entries.Count(e => !e.Source.IsDownloadSource());
            var downloadCount = _services.Data.Entries.Count(e => e.Source.IsDownloadSource());
            var sections = _services.Data.NoteSections?.Count ?? 0;
            var pages = _services.Data.NoteSections?.Sum(s => s.Pages.Count) ?? 0;
            return $"软件列表 {appCount} 条 · 软件下载 {downloadCount} 条 · SinNote {sections} 分区 / {pages} 页";
        }
    }

    /// <summary>备份/恢复后刷新顶部统计文案。</summary>
    public void RefreshSummaryTexts()
        => RaisePropertyChanged(nameof(LastBackupText), nameof(EntryCountText), nameof(AutoBackupStatusText));

    public ObservableCollection<BackupFileInfo> Backups { get; } = new();

    public BackupFileInfo? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (SetProperty(ref _selectedBackup, value))
            {
                (RestoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (DeleteBackupCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    // ---------- 命令 ----------

    public ICommand TestConnectionCommand { get; }
    public ICommand BackupNowCommand { get; }
    public ICommand RefreshListCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand DeleteBackupCommand { get; }
    public ICommand ExportLocalCommand { get; }
    public ICommand ImportLocalCommand { get; }
    public ICommand OpenHelpCommand { get; }

    // ---------- 逻辑 ----------

    /// <summary>把界面上的 WebDAV 配置写回设置（不弹窗关闭）。</summary>
    public void SaveWebDavSettings()
    {
        var s = _services.Settings;
        s.WebDavUrl = string.IsNullOrWhiteSpace(WebDavUrl) ? "https://dav.jianguoyun.com/dav/" : WebDavUrl.Trim();
        s.WebDavUser = WebDavUser.Trim();

        // 坚果云应用密码是 16 位小写字母+数字；复制时可能带上空格/换行，这里统一去掉首尾空白
        s.WebDavPassword = (WebDavPassword ?? string.Empty).Trim();
        s.WebDavFolder = string.IsNullOrWhiteSpace(WebDavFolder) ? "SinUpdateHub" : WebDavFolder.Trim();
        s.KeepBackups = KeepBackups;
        s.AutoBackupEnabled = AutoBackupEnabled;
        s.AutoBackupIntervalHours = Math.Clamp(AutoBackupIntervalHours, 1, 720);
        _services.ApplySettings(s);
        OnPropertyChanged(nameof(AutoBackupStatusText));
    }

    /// <summary>由主窗口定时器或启动后延迟调用：若已到达自动备份周期则静默上传一份 WebDAV 备份。</summary>
    public async Task<bool> TryAutoBackupAsync()
    {
        if (IsBusy || _isAutoBackingUp)
        {
            return false;
        }

        if (!_services.Settings.IsAutoBackupDue(DateTimeOffset.Now))
        {
            return false;
        }

        _isAutoBackingUp = true;
        try
        {
            BeforeBackup?.Invoke();
            _services.SaveData();
            var result = await _services.WebDav.UploadAsync(_services.Settings, _services.Data);
            if (result.Ok)
            {
                _services.Settings.LastBackupAt = DateTimeOffset.Now;
                _services.SaveSettings();
                RefreshSummaryTexts();
                var summary = $"已自动备份到 WebDAV（{DateTime.Now:HH:mm}）";
                StatusMessage = $"{result.Message}（自动备份，已清理 {result.PrunedCount} 个旧备份）";
                AppLog.Write($"WebDAV 自动定时备份成功：{result.Message}");
                AutoBackupCompleted?.Invoke(summary);
                return true;
            }

            StatusMessage = "自动备份失败：" + result.Message;
            AppLog.Write($"WebDAV 自动定时备份失败：{result.Message}");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Write("WebDAV 自动定时备份异常", ex);
            StatusMessage = "自动备份失败：" + ex.Message;
            return false;
        }
        finally
        {
            _isAutoBackingUp = false;
        }
    }

    /// <summary>打开坚果云「如何获取应用密码」帮助页。</summary>
    private void OpenJianguoyunHelp()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://help.jianguoyun.com/?p=2064",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Write("打开坚果云帮助页失败", ex);
        }
    }

    private async Task TestConnectionAsync()
    {
        SaveWebDavSettings();
        IsBusy = true;
        StatusMessage = "正在连接 WebDAV…";
        try
        {
            var result = await _services.WebDav.CheckAsync(_services.Settings);
            StatusMessage = result.Message;
            if (result.Ok)
            {
                await RefreshListAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("WebDAV 测试失败", ex);
            StatusMessage = "连接失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BackupNowAsync()
    {
        SaveWebDavSettings();
        IsBusy = true;
        StatusMessage = "正在上传备份…";
        try
        {
            BeforeBackup?.Invoke();
            _services.SaveData();
            var result = await _services.WebDav.UploadAsync(_services.Settings, _services.Data);
            StatusMessage = result.Message;
            if (result.Ok)
            {
                _services.Settings.LastBackupAt = DateTimeOffset.Now;
                _services.SaveSettings();
                RefreshSummaryTexts();
                StatusMessage += $"（已清理 {result.PrunedCount} 个旧备份）";
                await RefreshListAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("备份上传失败", ex);
            StatusMessage = "备份失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshListAsync()
    {
        if (string.IsNullOrWhiteSpace(WebDavUrl))
        {
            StatusMessage = "请先填写 WebDAV 地址。";
            return;
        }

        IsBusy = true;
        try
        {
            var list = await _services.WebDav.ListAsync(_services.Settings);
            Backups.Clear();
            foreach (var item in list)
            {
                Backups.Add(item);
            }

            // 自动选中最新一条，用户不用先点列表也能直接点「恢复选中备份」
            SelectedBackup = Backups.FirstOrDefault();

            StatusMessage = Backups.Count == 0
                ? "远程目录里还没有备份文件。"
                : $"找到 {Backups.Count} 个备份文件（已选中最新一条，点「恢复选中备份」即可恢复）。";
        }
        catch (Exception ex)
        {
            AppLog.Write("列出备份失败", ex);
            StatusMessage = "读取备份列表失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAsync()
    {
        if (SelectedBackup is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "正在下载备份…";
        AppData downloaded;
        try
        {
            SaveWebDavSettings();
            downloaded = await _services.WebDav.DownloadAsync(_services.Settings, SelectedBackup.FileName);
        }
        catch (Exception ex)
        {
            AppLog.Write("下载远端备份失败", ex);
            StatusMessage = "恢复失败：" + ex.Message;
            IsBusy = false;
            return;
        }

        IsBusy = false;
        ApplySelectiveRestore(SelectedBackup.FileName, downloaded, isWebDavRestore: true);
    }

    private async Task DeleteBackupAsync()
    {
        if (SelectedBackup is null)
        {
            return;
        }

        var ok = Confirm?.Invoke("删除备份", $"确定删除远端备份「{SelectedBackup.FileName}」？") ?? false;
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _services.WebDav.DeleteAsync(_services.Settings, SelectedBackup.FileName);
            StatusMessage = "已删除：" + SelectedBackup.FileName;
            await RefreshListAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("删除备份失败", ex);
            StatusMessage = "删除失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportLocalAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出清单",
            Filter = "SinUpdateHub 备份 (*.json)|*.json",
            FileName = $"SinUpdateHub-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            BeforeBackup?.Invoke();
            _services.AppStore.Export(_services.Data, dialog.FileName);
            StatusMessage = "已导出到：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            AppLog.Write("导出失败", ex);
            StatusMessage = "导出失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        await Task.CompletedTask;
    }

    private async Task ImportLocalAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入清单",
            Filter = "SinUpdateHub 备份 (*.json)|*.json|所有文件 (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        AppData imported;
        try
        {
            imported = _services.AppStore.Import(dialog.FileName);
        }
        catch (Exception ex)
        {
            AppLog.Write("读取导入文件失败", ex);
            StatusMessage = "导入失败：" + ex.Message;
            return;
        }

        var sourceName = System.IO.Path.GetFileName(dialog.FileName);
        ApplySelectiveRestore(sourceName, imported, isWebDavRestore: false);
        await Task.CompletedTask;
    }

    private void ApplySelectiveRestore(string sourceName, AppData incoming, bool isWebDavRestore)
    {
        RestoreSelectionResult? selection;
        if (SelectRestoreItems is not null)
        {
            selection = SelectRestoreItems.Invoke(sourceName, incoming);
            if (selection is null || !selection.HasAnySelection)
            {
                StatusMessage = "已取消恢复。";
                return;
            }
        }
        else
        {
            var ok = Confirm?.Invoke("恢复数据", $"确定用「{sourceName}」覆盖当前数据吗？") ?? false;
            if (!ok)
            {
                return;
            }

            selection = new RestoreSelectionResult
            {
                SelectedEntryIds = (incoming.Entries ?? new List<AppEntry>()).Select(e => e.Id).ToHashSet(StringComparer.Ordinal),
                SelectedNotePageIds = NoteSection.NormalizeList(incoming.NoteSections)
                    .SelectMany(s => s.Pages)
                    .Select(p => p.Id)
                    .ToHashSet(StringComparer.Ordinal),
                Mode = RestoreMode.Replace,
            };
        }

        IsBusy = true;
        try
        {
            // 先把内存中的 SinNote 未落盘编辑刷回 _services.Data，避免合并时丢失刚编辑的内容
            BeforeBackup?.Invoke();

            var (mergedData, summary) = DataMergeService.Apply(_services.Data, incoming, selection);
            _services.Data = mergedData;
            _services.SaveData();
            if (isWebDavRestore)
            {
                _services.Settings.LastRestoreAt = DateTimeOffset.Now;
                _services.SaveSettings();
            }

            DataRestored?.Invoke();
            RefreshSummaryTexts();

            var modeText = summary.Mode == RestoreMode.Merge ? "合并恢复" : "覆盖恢复";
            StatusMessage = $"已{modeText}：软件列表 {summary.RestoredAppsCount} 条、软件下载 {summary.RestoredDownloadsCount} 条、SinNote {summary.RestoredNoteSectionsCount} 分区（{summary.RestoredNotePagesCount} 页）。";
        }
        catch (Exception ex)
        {
            AppLog.Write("恢复数据失败", ex);
            StatusMessage = "恢复失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
