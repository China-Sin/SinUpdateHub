using System.IO;
using System.Windows.Input;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.ViewModels;

/// <summary>
/// 主界面卡片列表里的一行：包装 <see cref="AppEntry"/>，附加运行期状态与命令。
/// </summary>
public sealed class EntryViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private UpdateState _state = UpdateState.Unknown;
    private bool _isDownloading;
    private double _progress;
    private string? _transferText;
    private string _filterText = string.Empty;

    public EntryViewModel(MainViewModel owner, AppEntry entry)
    {
        _owner = owner;
        Entry = entry;

        CheckCommand = new AsyncRelayCommand(() => owner.CheckOneAsync(this));
        UpdateCommand = new AsyncRelayCommand(() => owner.UpdateOneAsync(this));
        EditCommand = new RelayCommand(() => owner.EditEntry(this));
        DeleteCommand = new RelayCommand(() => owner.DeleteEntry(this));
        OpenFolderCommand = new RelayCommand(() => owner.OpenInstallDir(this));
        OpenReleaseCommand = new RelayCommand(() => owner.OpenReleasePage(this));
        CopyInfoCommand = new RelayCommand(() => owner.CopyInfo(this));
        ChooseAssetCommand = new AsyncRelayCommand(() => owner.ChooseAssetAsync(this));

        RefreshFromEntry();
        EvaluateState();
    }

    public AppEntry Entry { get; }

    // ---------- 基础字段 ----------

    public string Name => !string.IsNullOrWhiteSpace(Entry.DisplayName)
        ? Entry.DisplayName
        : string.IsNullOrWhiteSpace(Entry.Name) ? "(未命名)" : Entry.Name;

    public SourceKind Source => Entry.Source;

    public bool IsCustomSource => Entry.Source == SourceKind.Custom;

    public string SourceText => Entry.Source switch
    {
        SourceKind.GitHub => "GitHub",
        SourceKind.WinGet => "WinGet",
        _ => "官网",
    };

    /// <summary>GitHub 显示 owner/repo；WinGet 显示备注（包 ID 在「包」列里，不重复显示）。</summary>
    public string RepositoryText
    {
        get
        {
            if (Entry.Source == SourceKind.WinGet)
            {
                return Entry.Remark;
            }

            if (Entry.Source == SourceKind.Custom)
            {
                if (!string.IsNullOrWhiteSpace(Entry.HomepageUrl)
                    && Uri.TryCreate(Entry.HomepageUrl, UriKind.Absolute, out var homepage))
                {
                    return homepage.Host;
                }

                return "手动来源（需填官网下载页）";
            }

            var parsed = AssetMatcher.ParseGitHubRepo(Entry.RepoUrl);
            return parsed is null ? (string.IsNullOrWhiteSpace(Entry.RepoUrl) ? "(未填仓库)" : Entry.RepoUrl) : $"{parsed.Value.Owner}/{parsed.Value.Repo}";
        }
    }

    /// <summary>包筛选条件（GitHub）或包 ID（WinGet），表格「包」列直接显示。</summary>
    public string PackageText
    {
        get
        {
            if (Entry.Source == SourceKind.Custom)
            {
                return "手动来源";
            }

            if (Entry.Source == SourceKind.WinGet)
            {
                return string.IsNullOrWhiteSpace(Entry.Package) ? "未选包" : Entry.Package;
            }

            return string.IsNullOrWhiteSpace(Entry.Package) ? "自动匹配" : Entry.Package;
        }
    }

    public bool IsNotInstalled
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Entry.ExePath))
            {
                return InstalledAppDetector.IsWindowsAppsAliasPath(Entry.ExePath)
                    ? !File.Exists(Entry.ExePath) && string.IsNullOrWhiteSpace(Entry.CurrentVersion)
                    : !File.Exists(Entry.ExePath);
            }

            return string.IsNullOrWhiteSpace(Entry.InstallDir) || !Directory.Exists(Entry.InstallDir);
        }
    }

    public string CurrentVersionText => IsNotInstalled
        ? "未安装"
        : string.IsNullOrWhiteSpace(Entry.CurrentVersion) ? "未检测到" : Entry.CurrentVersion;

    public string LatestVersionText => string.IsNullOrWhiteSpace(Entry.LatestVersion) ? "—" : Entry.LatestVersion;

    public bool HasCurrentVersion => !string.IsNullOrWhiteSpace(Entry.CurrentVersion);

    public string InstallDirText => string.IsNullOrWhiteSpace(Entry.InstallDir) ? "未设置安装目录" : Entry.InstallDir;

    public bool HasInstallDir => !string.IsNullOrWhiteSpace(Entry.InstallDir);

    public string InstallArgsText => string.IsNullOrWhiteSpace(Entry.InstallArgs) ? string.Empty : "安装参数：" + Entry.InstallArgs;

    public bool HasInstallArgs => !string.IsNullOrWhiteSpace(Entry.InstallArgs);

    public string MatchedAssetText => string.IsNullOrWhiteSpace(Entry.MatchedAssetName) ? string.Empty : "匹配到：" + Entry.MatchedAssetName;

    public bool HasMatchedAsset => !string.IsNullOrWhiteSpace(Entry.MatchedAssetName);

    public string LastCheckedText => Entry.LastCheckedAt is null
        ? string.Empty
        : "上次检查 " + Entry.LastCheckedAt.Value.LocalDateTime.ToString("MM-dd HH:mm");

    public string? LastError => Entry.LastError;

    public bool HasError => !IsCustomSource && (!string.IsNullOrWhiteSpace(Entry.LastError) || _state == UpdateState.Failed);

    public bool ShowLastChecked => !IsCustomSource && !HasError;

    public string ErrorText => Entry.LastError ?? string.Empty;

    public UpdateState State => _state;

    /// <summary>状态排序权重：可更新 → 检查失败 → 未安装 → 检查中 → 未检查 → 已是最新/已安装。</summary>
    public int StateSortOrder
    {
        get
        {
            if (Entry.Source == SourceKind.Custom)
            {
                return IsNotInstalled ? 2 : 5;
            }

            if (_state == UpdateState.Checking)
            {
                return 3;
            }

            if (_state == UpdateState.Failed)
            {
                return 1;
            }

            if (IsNotInstalled)
            {
                return 2;
            }

            return _state switch
            {
                UpdateState.UpdateAvailable => 0,
                UpdateState.UpToDate => 5,
                UpdateState.Failed => 1,
                _ => 4,
            };
        }
    }

    public bool HasUpdate => _state == UpdateState.UpdateAvailable && !_isDownloading;

    /// <summary>是否显示操作列的「更新/打开官网」按钮（官网来源没有自动版本，也要能点）。</summary>
    public bool ShowUpdateAction => HasUpdate || IsCustomSource;

    /// <summary>操作列按钮文字：手动来源显示「打开网页」，未安装显示「安装」。</summary>
    public string UpdateButtonText => IsCustomSource ? "打开网页" : (IsNotInstalled ? "安装" : "更新");

    public bool IsUpToDate => _state == UpdateState.UpToDate;

    public bool IsChecking => _state == UpdateState.Checking;

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                RaisePropertyChanged(nameof(HasUpdate), nameof(ShowUpdateAction), nameof(IsBusyVisual));
            }
        }
    }

    public bool IsBusyVisual => _isDownloading || _state == UpdateState.Checking;

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string? TransferText
    {
        get => _transferText;
        private set => SetProperty(ref _transferText, value);
    }

    public string StateText
    {
        get
        {
            if (Entry.Source == SourceKind.Custom)
            {
                return IsNotInstalled ? "未安装" : "已安装";
            }

            if (_state == UpdateState.Checking)
            {
                return "检查中…";
            }

            if (_state == UpdateState.Failed)
            {
                return "检查失败";
            }

            if (IsNotInstalled)
            {
                return "未安装";
            }

            return _state switch
            {
                UpdateState.UpdateAvailable => "可更新",
                UpdateState.UpToDate => "已是最新",
                UpdateState.Failed => "检查失败",
                _ => "未检查",
            };
        }
    }

    /// <summary>版本展示：4.33.0.8907 → 4.48.2.5754。</summary>
    public string VersionArrowText
    {
        get
        {
            var current = string.IsNullOrWhiteSpace(Entry.CurrentVersion) ? "?" : Entry.CurrentVersion;
            if (Entry.Source == SourceKind.Custom)
            {
                return current;
            }
            if (_state == UpdateState.UpdateAvailable && !string.IsNullOrWhiteSpace(Entry.LatestVersion))
            {
                return $"{current}  →  {Entry.LatestVersion}";
            }

            if (!string.IsNullOrWhiteSpace(Entry.LatestVersion))
            {
                return $"{current}  （远端 {Entry.LatestVersion}）";
            }

            return current;
        }
    }

    public string SearchBlob => string.Join(' ', new[]
    {
        Entry.Name,
        Entry.DisplayName,
        Entry.Package,
        Entry.RepoUrl,
        Entry.CurrentVersion,
        Entry.LatestVersion ?? string.Empty,
        Entry.InstallDir,
        Entry.Remark,
        SourceText,
    }).ToLowerInvariant();

    // ---------- 命令 ----------

    public ICommand CheckCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenReleaseCommand { get; }
    public ICommand CopyInfoCommand { get; }
    public ICommand ChooseAssetCommand { get; }

    // ---------- 状态更新 ----------

    /// <summary>把模型字段重新读出来刷新界面。</summary>
    public void RefreshFromEntry()
    {
        RaisePropertyChanged(
            nameof(Name), nameof(Source), nameof(IsCustomSource), nameof(IsNotInstalled), nameof(SourceText),
            nameof(RepositoryText), nameof(PackageText), nameof(CurrentVersionText), nameof(LatestVersionText),
            nameof(HasCurrentVersion), nameof(InstallDirText), nameof(HasInstallDir), nameof(InstallArgsText),
            nameof(HasInstallArgs), nameof(MatchedAssetText), nameof(HasMatchedAsset), nameof(LastCheckedText),
            nameof(LastError), nameof(HasError), nameof(ShowLastChecked), nameof(ErrorText), nameof(StateText), nameof(StateSortOrder), nameof(VersionArrowText),
            nameof(ShowUpdateAction), nameof(UpdateButtonText), nameof(SearchBlob));
    }

    /// <summary>根据当前条目快照推导运行期状态。</summary>
    public void EvaluateState()
    {
        if (IsDownloading || _state == UpdateState.Checking)
        {
            return;
        }

        if (Entry.Source == SourceKind.Custom)
        {
            SetState(IsNotInstalled ? UpdateState.Unknown : UpdateState.UpToDate);
            return;
        }

        if (IsNotInstalled)
        {
            if (!string.IsNullOrWhiteSpace(Entry.LastError))
            {
                SetState(UpdateState.Failed);
                return;
            }

            SetState(!string.IsNullOrWhiteSpace(Entry.LatestVersion)
                ? UpdateState.UpdateAvailable
                : UpdateState.Unknown);
            return;
        }

        if (!string.IsNullOrWhiteSpace(Entry.LastError))
        {
            SetState(UpdateState.Failed);
            return;
        }

        if (string.IsNullOrWhiteSpace(Entry.LatestVersion))
        {
            SetState(UpdateState.Unknown);
            return;
        }

        SetState(VersionComparer.IsNewer(Entry.CurrentVersion, Entry.LatestVersion)
            ? UpdateState.UpdateAvailable
            : UpdateState.UpToDate);
    }

    public void SetState(UpdateState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        RaisePropertyChanged(
            nameof(State), nameof(StateText), nameof(StateSortOrder), nameof(IsNotInstalled), nameof(HasUpdate), nameof(ShowUpdateAction),
            nameof(UpdateButtonText), nameof(IsUpToDate), nameof(IsChecking), nameof(IsBusyVisual),
            nameof(HasError), nameof(ShowLastChecked), nameof(VersionArrowText));
    }

    public void MarkChecking()
    {
        _state = UpdateState.Checking;
        RaisePropertyChanged(
            nameof(State), nameof(StateText), nameof(StateSortOrder), nameof(IsNotInstalled), nameof(HasUpdate), nameof(ShowUpdateAction),
            nameof(UpdateButtonText), nameof(IsUpToDate), nameof(IsChecking), nameof(IsBusyVisual),
            nameof(VersionArrowText));
    }

    public void MarkDownloading(bool downloading)
    {
        IsDownloading = downloading;
        if (!downloading)
        {
            Progress = 0;
            TransferText = null;
        }
    }

    public void SetProgress(double fraction, string? text)
    {
        Progress = Math.Clamp(fraction, 0, 1);
        if (!string.IsNullOrWhiteSpace(text))
        {
            TransferText = text;
        }
    }

    public void ApplyCheckResult(CheckResult result)
    {
        UpdateChecker.Apply(Entry, result);
        RefreshFromEntry();
        SetState(result.State);
    }

    public override string ToString() => Name;
}
