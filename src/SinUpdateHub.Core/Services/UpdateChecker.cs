using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// 批量检查编排：按 <see cref="AppEntry.Source"/> 分派给对应的
/// <see cref="IUpdateSourceProvider"/>，把异常统一转换成失败状态，
/// 并把结果回填到条目快照字段（新版本 / 下载地址 / 检查时间 / 错误）。
/// 支持并发检查（默认 4 路）与逐条进度回调。
/// </summary>
public sealed class UpdateChecker
{
    private readonly Dictionary<SourceKind, IUpdateSourceProvider> _providers;

    /// <param name="providers">各来源实现（GitHub / WinGet）；同一来源重复注册时以最后一个为准。</param>
    public UpdateChecker(IEnumerable<IUpdateSourceProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = new Dictionary<SourceKind, IUpdateSourceProvider>();
        foreach (var provider in providers)
        {
            if (provider is null)
            {
                continue;
            }

            _providers[provider.Kind] = provider;
        }
    }

    /// <summary>检查单个条目：成功返回状态与最新版本，失败返回 <see cref="UpdateState.Failed"/>（不抛异常）。</summary>
    public async Task<CheckResult> CheckOneAsync(AppEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            if (!_providers.TryGetValue(entry.Source, out var provider))
            {
                throw new UpdateSourceException($"不支持的来源类型：{entry.Source}，请检查条目配置");
            }

            // 传给来源的是副本，避免来源实现（或未来扩展）误改用户条目
            var update = await provider.GetLatestAsync(entry.Clone(), ct).ConfigureAwait(false);
            if (update is null)
            {
                throw new UpdateSourceException("来源没有返回检查结果，请稍后重试");
            }

            var state = Evaluate(entry, update);
            var result = new CheckResult
            {
                EntryId = entry.Id,
                State = state,
                Update = update,
                CheckedAt = DateTimeOffset.Now,
            };

            if (state == UpdateState.Failed)
            {
                result.Error = "检查结果里没有版本号（来源可能返回了空标签），请确认该软件的发布信息";
                AppLog.Write($"检查更新失败：{entry.Name}（{entry.Source}）→ {result.Error}");
            }

            Apply(entry, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write($"检查更新失败：{entry.Name}（{entry.Source} / {entry.Package}）", ex);
            var result = CheckResult.Fail(entry.Id, ex.Message);
            Apply(entry, result);
            return result;
        }
    }

    /// <summary>
    /// 并发检查一批条目。每完成一条通过 <paramref name="progress"/> 回调一次（<see cref="Progress{T}"/>
    /// 会自动回到创建它的 UI 线程）。返回顺序与入参一致。
    /// </summary>
    public async Task<IReadOnlyList<CheckResult>> CheckAllAsync(
        IReadOnlyList<AppEntry> entries,
        IProgress<CheckResult>? progress = null,
        int maxParallel = 4,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (maxParallel < 1)
        {
            maxParallel = 1;
        }

        var results = new CheckResult[entries.Count];
        using var gate = new SemaphoreSlim(maxParallel, maxParallel);

        var tasks = new List<Task>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var index = i;
            var entry = entries[i];
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await CheckOneAsync(entry, ct).ConfigureAwait(false);
                    results[index] = result;
                    progress?.Report(result);
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// 根据条目当前版本与远端版本得出状态：
    /// 远端版本为空 → <see cref="UpdateState.Failed"/>；
    /// 本地版本为空 → 按可更新处理并补提示；
    /// 远端更新 → <see cref="UpdateState.UpdateAvailable"/>；相等或本地更高 → <see cref="UpdateState.UpToDate"/>。
    /// </summary>
    public static UpdateState Evaluate(AppEntry entry, AvailableUpdate update)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(update);

        // 官网/手动与百度网盘来源：没有自动版本号，状态保持"未检查/手动"，不算失败
        if (entry.Source.IsDownloadSource())
        {
            return UpdateState.Unknown;
        }

        if (string.IsNullOrWhiteSpace(VersionComparer.Normalize(update.Version)))
        {
            return UpdateState.Failed;
        }

        if (string.IsNullOrWhiteSpace(VersionComparer.Normalize(entry.CurrentVersion)))
        {
            update.Advisory = AppendAdvisory(update.Advisory, "当前版本未填，无法比较，按可更新处理");
            return UpdateState.UpdateAvailable;
        }

        return VersionComparer.IsNewer(entry.CurrentVersion, update.Version)
            ? UpdateState.UpdateAvailable
            : UpdateState.UpToDate;
    }

    /// <summary>
    /// 把检查结果回填到条目快照字段。
    /// 失败时只写 <see cref="AppEntry.LastError"/> 与 <see cref="AppEntry.LastCheckedAt"/>，
    /// 保留上次成功检查得到的新版本 / 下载地址，避免一次临时断网就丢历史信息。
    /// </summary>
    public static void Apply(AppEntry entry, CheckResult result)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(result);

        entry.LastCheckedAt = result.CheckedAt;

        // 官网/手动与百度网盘来源：只记录检查时间，不动版本/下载地址
        if (entry.Source.IsDownloadSource())
        {
            entry.LastError = result.State == UpdateState.Failed ? (result.Error ?? "检查失败") : null;
            if (entry.Source == SourceKind.Custom && !string.IsNullOrWhiteSpace(result.Update?.HomepageUrl))
            {
                entry.HomepageUrl = result.Update!.HomepageUrl;
            }

            return;
        }

        if (result.State == UpdateState.Failed)
        {
            entry.LastError = string.IsNullOrWhiteSpace(result.Error) ? "检查失败（原因未知）" : result.Error;
            return;
        }

        var update = result.Update;
        entry.LatestVersion = string.IsNullOrWhiteSpace(update?.Version) ? null : update!.Version;
        entry.MatchedAssetName = update?.AssetName;
        entry.DownloadUrl = update?.DownloadUrl;
        entry.ReleasePageUrl = update?.ReleasePageUrl;
        if (!string.IsNullOrWhiteSpace(update?.HomepageUrl))
        {
            entry.HomepageUrl = update!.HomepageUrl;
        }
        entry.LastError = null;
    }

    private static string AppendAdvisory(string? existing, string extra)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return extra;
        }

        return existing.Contains(extra, StringComparison.Ordinal) ? existing : existing + "；" + extra;
    }

    /// <summary>
    /// 当 EXE 文件版本与注册表/软件版本不一致时，对比远端最新版本：
    /// 有一个与远端最新版本一致就优先采用一致的那个；都不一致时优先采用 EXE 版本并正常提示更新。
    /// </summary>
    public static string? ReconcileVersionWithLatest(string? exeVersion, string? softwareVersion, string? latestVersion)
    {
        var exe = string.IsNullOrWhiteSpace(exeVersion) ? null : exeVersion.Trim();
        var software = string.IsNullOrWhiteSpace(softwareVersion) ? null : softwareVersion.Trim();

        if (exe is not null && software is not null && VersionComparer.Compare(exe, software) != 0)
        {
            if (!string.IsNullOrWhiteSpace(latestVersion))
            {
                if (VersionComparer.Compare(exe, latestVersion) == 0)
                {
                    return exe;
                }

                if (VersionComparer.Compare(software, latestVersion) == 0)
                {
                    return software;
                }
            }

            return exe;
        }

        return exe ?? software;
    }
}
