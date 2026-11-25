using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App;

/// <summary>
/// 极简服务容器：集中 new 各服务，供 ViewModel 使用。
/// 想扩展新功能时，在这里加一个服务字段即可。
/// </summary>
public sealed class AppServices
{
    public static AppServices Current { get; private set; } = null!;

    private AppServices()
    {
    }

    public static AppServices Initialize()
    {
        var svc = new AppServices();

        svc.SettingsStore = new SettingsStore();
        svc.Settings = svc.SettingsStore.Load();

        svc.AppStore = new AppStore();
        svc.Data = svc.AppStore.Load();

        svc.Network = new NetworkClientFactory(() => svc.Settings);
        svc.WingetCatalog = new WingetCatalogService(svc.Network);

        svc.GitHub = new GitHubSourceProvider(svc.Network, () => svc.Settings);
        svc.Winget = new WingetSourceProvider(svc.Network, () => svc.Settings, svc.WingetCatalog);
        svc.Checker = new UpdateChecker(new IUpdateSourceProvider[] { svc.GitHub, svc.Winget, new ManualSourceProvider(svc.Network) });

        svc.Detector = new InstalledAppDetector();
        svc.Downloader = new DownloadService(svc.Network, () => svc.Settings);
        svc.Installer = new InstallerRunner(() => svc.Settings);
        svc.WebDav = new WebDavBackupService(svc.Network, () => svc.Settings);

        AppPaths.EnsureAll(svc.Settings.DownloadDir);
        Current = svc;
        return svc;
    }

    public ISettingsStore SettingsStore { get; private set; } = null!;
    public IAppStore AppStore { get; private set; } = null!;
    public SettingsStore ConcreteSettingsStore => (SettingsStore)SettingsStore;

    public AppSettings Settings { get; set; } = new();
    public AppData Data { get; set; } = new();

    public NetworkClientFactory Network { get; private set; } = null!;
    public WingetCatalogService WingetCatalog { get; private set; } = null!;
    public GitHubSourceProvider GitHub { get; private set; } = null!;
    public WingetSourceProvider Winget { get; private set; } = null!;
    public UpdateChecker Checker { get; private set; } = null!;
    public IInstalledAppDetector Detector { get; private set; } = null!;
    public IDownloadService Downloader { get; private set; } = null!;
    public IInstallerRunner Installer { get; private set; } = null!;
    public IWebDavBackupService WebDav { get; private set; } = null!;

    public void SaveSettings()
    {
        SettingsStore.Save(Settings);
    }

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        SaveSettings();
    }

    public void SaveData()
    {
        Data.UpdatedAt = DateTimeOffset.Now;
        AppStore.Save(Data);
    }
}
