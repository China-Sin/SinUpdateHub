using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.ViewModels;

/// <summary>代理模式下拉项。</summary>
public sealed class ProxyModeOption
{
    public ProxyModeOption(ProxyMode mode, string label, string hint)
    {
        Mode = mode;
        Label = label;
        Hint = hint;
    }

    public ProxyMode Mode { get; }
    public string Label { get; }
    public string Hint { get; }

    public override string ToString() => Label;
}

/// <summary>设置页 ViewModel（内嵌在主窗口右侧内容区，不再是独立窗口）。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private AppSettings _draft;

    private ProxyModeOption _selectedProxyMode;
    private string _proxyUrl;
    private string _gitHubToken;
    private bool _includePrerelease;
    private bool _autoCheckOnStartup;
    private bool _runInstallerAsAdmin;
    private string _downloadDir;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    private string _tokenStatus = string.Empty;
    private bool _tokenOk;
    private bool _isVerifyingToken;

    private string _catalogStatusText;
    private double _catalogProgress;
    private bool _isUpdatingCatalog;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _draft = services.Settings.Clone();

        ProxyModes = new ObservableCollection<ProxyModeOption>
        {
            new(ProxyMode.System, "跟随系统代理（推荐）", "读取 Windows「Internet 选项」里的代理设置，大多数情况用这个。"),
            new(ProxyMode.Custom, "手动填写代理地址", "例如 http://127.0.0.1:7897；国内直连 GitHub 失败时用这个。"),
            new(ProxyMode.None, "不使用代理", "直连（公司内网/无代理环境）。"),
        };

        _selectedProxyMode = ProxyModes.FirstOrDefault(p => p.Mode == _draft.ProxyMode) ?? ProxyModes[0];
        _proxyUrl = _draft.ProxyUrl;
        _gitHubToken = _draft.GitHubToken;
        _includePrerelease = _draft.IncludePrerelease;
        _autoCheckOnStartup = _draft.AutoCheckOnStartup;
        _runInstallerAsAdmin = _draft.RunInstallerAsAdmin;
        _downloadDir = string.IsNullOrWhiteSpace(_draft.DownloadDir) ? AppPaths.DefaultDownloadDir : _draft.DownloadDir;
        _catalogStatusText = services.WingetCatalog.GetStatus().Description;

        // 如果系统里已经有 GH_TOKEN / GITHUB_TOKEN 环境变量且本地没填，自动填上（省得手动拷）
        if (string.IsNullOrWhiteSpace(_gitHubToken))
        {
            var envToken = Environment.GetEnvironmentVariable("GH_TOKEN")
                           ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(envToken))
            {
                _gitHubToken = envToken.Trim();
                _statusMessage = "已检测到系统环境变量 GITHUB_TOKEN/GH_TOKEN 并自动填入，点「保存」生效。";
            }
        }

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(ReloadFromStore);
        BrowseDownloadDirCommand = new RelayCommand(BrowseDownloadDir);
        TestNetworkCommand = new AsyncRelayCommand(TestNetworkAsync);
        ResetProxyCommand = new RelayCommand(() => ProxyUrl = string.Empty);
        OpenTokenPageCommand = new RelayCommand(OpenTokenPage);
        VerifyTokenCommand = new AsyncRelayCommand(VerifyTokenAsync, () => !IsVerifyingToken);
        UpdateCatalogCommand = new AsyncRelayCommand(UpdateCatalogAsync, () => !IsUpdatingCatalog);
    }

    /// <summary>设置已保存（主界面可据此刷新，例如启动自动检查开关）。</summary>
    public event Action? SettingsSaved;

    public ObservableCollection<ProxyModeOption> ProxyModes { get; }

    // ---------- 网络 ----------

    public ProxyModeOption SelectedProxyMode
    {
        get => _selectedProxyMode;
        set
        {
            if (SetProperty(ref _selectedProxyMode, value))
            {
                RaisePropertyChanged(nameof(IsCustomProxy));
            }
        }
    }

    public bool IsCustomProxy => SelectedProxyMode?.Mode == ProxyMode.Custom;

    public string ProxyUrl
    {
        get => _proxyUrl;
        set => SetProperty(ref _proxyUrl, value);
    }

    /// <summary>GitHub API Token（界面上用 PasswordBox 掩码显示）。</summary>
    public string GitHubToken
    {
        get => _gitHubToken;
        set
        {
            if (SetProperty(ref _gitHubToken, value))
            {
                TokenStatus = string.Empty;
                TokenOk = false;
            }
        }
    }

    /// <summary>Token 校验/账号信息（如「已登录：xxx（额度 4999/5000）」）。</summary>
    public string TokenStatus
    {
        get => _tokenStatus;
        private set
        {
            if (SetProperty(ref _tokenStatus, value))
            {
                RaisePropertyChanged(nameof(HasTokenStatus));
            }
        }
    }

    public bool HasTokenStatus => !string.IsNullOrWhiteSpace(_tokenStatus);

    /// <summary>Token 是否有效（有效时显示绿色「已登录」）。</summary>
    public bool TokenOk
    {
        get => _tokenOk;
        private set => SetProperty(ref _tokenOk, value);
    }

    public bool IsVerifyingToken
    {
        get => _isVerifyingToken;
        private set
        {
            if (SetProperty(ref _isVerifyingToken, value))
            {
                (VerifyTokenCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IncludePrerelease
    {
        get => _includePrerelease;
        set => SetProperty(ref _includePrerelease, value);
    }

    // ---------- 行为 ----------

    public bool AutoCheckOnStartup
    {
        get => _autoCheckOnStartup;
        set => SetProperty(ref _autoCheckOnStartup, value);
    }

    public bool RunInstallerAsAdmin
    {
        get => _runInstallerAsAdmin;
        set => SetProperty(ref _runInstallerAsAdmin, value);
    }

    public string DownloadDir
    {
        get => _downloadDir;
        set => SetProperty(ref _downloadDir, value);
    }

    // ---------- WinGet 本地索引 ----------

    public string CatalogStatusText
    {
        get => _catalogStatusText;
        private set => SetProperty(ref _catalogStatusText, value);
    }

    public double CatalogProgress
    {
        get => _catalogProgress;
        private set
        {
            if (SetProperty(ref _catalogProgress, value))
            {
                RaisePropertyChanged(nameof(IsCatalogBusyVisible));
            }
        }
    }

    public bool IsUpdatingCatalog
    {
        get => _isUpdatingCatalog;
        private set
        {
            if (SetProperty(ref _isUpdatingCatalog, value))
            {
                RaisePropertyChanged(nameof(IsCatalogBusyVisible));
                (UpdateCatalogCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCatalogBusyVisible => _isUpdatingCatalog && _catalogProgress > 0 && _catalogProgress < 1;

    // ---------- 通用 ----------

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                (SaveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (TestNetworkCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                RaisePropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(_statusMessage);

    public string VersionText => "SinUpdateHub 1.0.0";

    public string DataFileText => "清单：" + AppPaths.DataFile;

    public string SettingsFileText => "设置：" + AppPaths.SettingsFile;

    // ---------- 命令 ----------

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseDownloadDirCommand { get; }
    public ICommand TestNetworkCommand { get; }
    public ICommand ResetProxyCommand { get; }
    public ICommand OpenTokenPageCommand { get; }
    public ICommand VerifyTokenCommand { get; }
    public ICommand UpdateCatalogCommand { get; }

    // ---------- 逻辑 ----------

    private void BrowseDownloadDir()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载缓存目录",
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(DownloadDir) && Directory.Exists(DownloadDir))
        {
            dialog.InitialDirectory = DownloadDir;
        }

        if (dialog.ShowDialog() == true)
        {
            DownloadDir = dialog.FolderName;
        }
    }

    private void ApplyToSettings()
    {
        _draft.ProxyMode = SelectedProxyMode.Mode;
        _draft.ProxyUrl = ProxyUrl.Trim();
        _draft.GitHubToken = GitHubToken.Trim();
        _draft.IncludePrerelease = IncludePrerelease;
        _draft.AutoCheckOnStartup = AutoCheckOnStartup;
        _draft.RunInstallerAsAdmin = RunInstallerAsAdmin;
        _draft.DownloadDir = DownloadDir.Trim();
    }

    private async Task SaveAsync()
    {
        ApplyToSettings();
        IsBusy = true;
        try
        {
            _services.ApplySettings(_draft);
            AppPaths.EnsureAll(_draft.DownloadDir);
            StatusMessage = "设置已保存。";
            SettingsSaved?.Invoke();

            if (!string.IsNullOrWhiteSpace(GitHubToken) && !TokenOk)
            {
                await VerifyTokenAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("保存设置失败", ex);
            StatusMessage = "保存失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>撤销未保存的修改，重新从磁盘读取。</summary>
    private void ReloadFromStore()
    {
        _services.Settings = _services.SettingsStore.Load();
        _draft = _services.Settings.Clone();
        _selectedProxyMode = ProxyModes.FirstOrDefault(p => p.Mode == _draft.ProxyMode) ?? ProxyModes[0];
        _proxyUrl = _draft.ProxyUrl;
        _gitHubToken = _draft.GitHubToken;
        _includePrerelease = _draft.IncludePrerelease;
        _autoCheckOnStartup = _draft.AutoCheckOnStartup;
        _runInstallerAsAdmin = _draft.RunInstallerAsAdmin;
        _downloadDir = string.IsNullOrWhiteSpace(_draft.DownloadDir) ? AppPaths.DefaultDownloadDir : _draft.DownloadDir;
        _tokenStatus = string.Empty;
        _tokenOk = false;
        _statusMessage = "已撤销未保存的修改。";
        RaisePropertyChanged(
            nameof(SelectedProxyMode), nameof(ProxyUrl), nameof(GitHubToken), nameof(IncludePrerelease),
            nameof(AutoCheckOnStartup), nameof(RunInstallerAsAdmin), nameof(DownloadDir),
            nameof(TokenStatus), nameof(HasTokenStatus), nameof(TokenOk), nameof(StatusMessage));
    }

    private void OpenTokenPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/settings/tokens/new?description=SinUpdateHub%20(read-only%20API)",
                UseShellExecute = true,
            });
            StatusMessage = "已用浏览器打开 GitHub Token 页面：不勾任何权限，点 Generate token，把结果粘贴到上面的输入框，再点「验证」。";
        }
        catch (Exception ex)
        {
            AppLog.Write("打开 Token 页面失败", ex);
            StatusMessage = "打开浏览器失败：" + ex.Message;
        }
    }

    /// <summary>验证 Token：调 /user 拿账号名 + /rate_limit 拿额度。</summary>
    private async Task VerifyTokenAsync()
    {
        var token = GitHubToken.Trim();
        if (token.Length == 0)
        {
            TokenOk = false;
            TokenStatus = "未填写 Token：匿名请求限 60 次/小时（按 IP 算）";
            return;
        }

        IsVerifyingToken = true;
        TokenStatus = "正在验证 Token…";
        try
        {
            var probeSettings = _draft.Clone();
            probeSettings.GitHubToken = token;
            using var client = NetworkClientFactory.CreateClient(probeSettings, TimeSpan.FromSeconds(15));

            using (var userResponse = await client.GetAsync("https://api.github.com/user"))
            {
                if (userResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    TokenOk = false;
                    TokenStatus = "Token 无效或已过期，请重新生成。";
                    return;
                }

                if (!userResponse.IsSuccessStatusCode)
                {
                    TokenOk = false;
                    TokenStatus = $"验证失败（HTTP {(int)userResponse.StatusCode}），请检查代理设置。";
                    return;
                }

                var userJson = await userResponse.Content.ReadAsStringAsync();
                var login = string.Empty;
                using (var doc = System.Text.Json.JsonDocument.Parse(userJson))
                {
                    if (doc.RootElement.TryGetProperty("login", out var loginElement))
                    {
                        login = loginElement.GetString() ?? string.Empty;
                    }
                }

                var quota = string.Empty;
                using var rateResponse = await client.GetAsync("https://api.github.com/rate_limit");
                if (rateResponse.IsSuccessStatusCode)
                {
                    var rateJson = await rateResponse.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(rateJson);
                    if (doc.RootElement.TryGetProperty("rate", out var rate) &&
                        rate.TryGetProperty("remaining", out var remaining) &&
                        rate.TryGetProperty("limit", out var limit))
                    {
                        quota = $"额度 {remaining.GetInt32()}/{limit.GetInt32()}";
                    }
                }

                TokenOk = true;
                TokenStatus = string.IsNullOrWhiteSpace(login)
                    ? $"Token 有效{(quota.Length > 0 ? "（" + quota + "）" : string.Empty)}"
                    : $"已登录：{login}{(quota.Length > 0 ? "（" + quota + "）" : string.Empty)}";
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("验证 GitHub Token 失败", ex);
            TokenOk = false;
            TokenStatus = "验证失败：" + ex.Message;
        }
        finally
        {
            IsVerifyingToken = false;
        }
    }

    /// <summary>下载 / 更新 WinGet 本地索引（按名称搜包 ID、WinGet 检查免 API 额度都要用它）。</summary>
    private async Task UpdateCatalogAsync()
    {
        IsUpdatingCatalog = true;
        CatalogProgress = 0;
        CatalogStatusText = "正在下载 WinGet 索引（约 21 MB）…";
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                CatalogProgress = p.Fraction;
                if (!string.IsNullOrWhiteSpace(p.Message))
                {
                    CatalogStatusText = p.Message!;
                }
            });

            var status = await _services.WingetCatalog.EnsureReadyAsync(forceUpdate: true, progress);
            CatalogStatusText = status.Description;
            StatusMessage = "WinGet 本地索引已更新。";
        }
        catch (Exception ex)
        {
            AppLog.Write("更新 WinGet 索引失败", ex);
            CatalogStatusText = "WinGet 索引更新失败：" + ex.Message;
        }
        finally
        {
            IsUpdatingCatalog = false;
            CatalogProgress = 0;
        }
    }

    private async Task TestNetworkAsync()
    {
        ApplyToSettings();
        IsBusy = true;
        StatusMessage = "正在测试网络…";
        try
        {
            var messages = new List<string>();
            using var factory = new NetworkClientFactory(() => _draft);

            // GitHub API（顺便读剩余额度，方便判断是否需要填 Token）
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var response = await factory.Client.GetAsync("https://api.github.com/rate_limit", cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cts.Token);
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("rate", out var rate) &&
                        rate.TryGetProperty("remaining", out var remaining) &&
                        rate.TryGetProperty("limit", out var limit))
                    {
                        messages.Add($"GitHub API 正常（剩余额度 {remaining.GetInt32()}/{limit.GetInt32()}）");
                    }
                    else
                    {
                        messages.Add("GitHub API 正常");
                    }
                }
                else
                {
                    messages.Add($"GitHub API 异常（HTTP {(int)response.StatusCode}）");
                }
            }
            catch (Exception ex)
            {
                messages.Add("GitHub API 连接失败：" + ex.Message);
            }

            // raw.githubusercontent（GitHub 来源下载/清单直链都走它）
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var response = await factory.Client.GetAsync("https://raw.githubusercontent.com/microsoft/winget-pkgs/master/README.md", cts.Token);
                messages.Add(response.IsSuccessStatusCode ? "raw.githubusercontent 可达" : $"raw.githubusercontent 异常（HTTP {(int)response.StatusCode}）");
            }
            catch (Exception ex)
            {
                messages.Add("raw.githubusercontent 连接失败：" + ex.Message);
            }

            // WinGet 本地索引
            messages.Add(_services.WingetCatalog.IsReady ? "WinGet 本地索引已就绪" : "WinGet 本地索引未下载");

            StatusMessage = "网络测试结果：" + string.Join(" ｜ ", messages);
        }
        catch (Exception ex)
        {
            StatusMessage = "测试失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>切到设置页时调用：刷新索引状态，并在已保存 Token 时自动验证一次。</summary>
    public async Task OnShownAsync()
    {
        CatalogStatusText = _services.WingetCatalog.GetStatus().Description;
        if (!TokenOk && !string.IsNullOrWhiteSpace(GitHubToken))
        {
            await VerifyTokenAsync();
        }
    }
}
