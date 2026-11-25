using System.IO.Compression;
using Microsoft.Data.Sqlite;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// WinGet 本地索引（微软官方 winget 源）：下载 21 MB 的 source.msix → 解出 45 MB 的 index.db（SQLite）。
/// 用途：
/// 1. 「按名称搜包 ID」——用户不用知道 NetEase.UURemote 这种 ID；
/// 2. 本地取最新版本号——WinGet 来源检查更新不再消耗 GitHub API 额度。
/// 索引缺失时会自动下载一次；之后每天最多自动刷新一次。
/// </summary>
public sealed class WingetCatalogService
{
    private const string SourceUrl = "https://cdn.winget.microsoft.com/cache/source.msix";
    private const string IndexEntryName = "Public/index.db";
    private static readonly TimeSpan AutoRefreshAfter = TimeSpan.FromDays(7);

    private readonly NetworkClientFactory _factory;
    private readonly string _indexPath;
    private readonly long _minReadyBytes;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly ReaderWriterLockSlim _dbLock = new();

    public WingetCatalogService(NetworkClientFactory factory)
        : this(factory, IndexPath, 1024 * 1024)
    {
    }

    internal WingetCatalogService(NetworkClientFactory factory, string indexPath, long minReadyBytes = 1024 * 1024)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _indexPath = indexPath ?? throw new ArgumentNullException(nameof(indexPath));
        _minReadyBytes = minReadyBytes;
    }

    /// <summary>本地 index.db 路径。</summary>
    public static string IndexPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppPaths.ProductName, "winget", "index.db");

    public WingetCatalogStatus GetStatus()
    {
        var path = _indexPath;
        var file = new FileInfo(path);
        return new WingetCatalogStatus
        {
            Ready = file.Exists && file.Length >= _minReadyBytes,
            IndexPath = path,
            SizeBytes = file.Exists ? file.Length : 0,
            UpdatedAt = file.Exists ? new DateTimeOffset(file.LastWriteTime) : null,
        };
    }

    /// <summary>索引是否已就绪（不触发下载）。</summary>
    public bool IsReady => GetStatus().Ready;

    /// <summary>索引是否存在但已过期（超过 7 天）。</summary>
    public bool IsStale
    {
        get
        {
            var status = GetStatus();
            return !status.Ready || status.UpdatedAt is null || DateTimeOffset.Now - status.UpdatedAt > AutoRefreshAfter;
        }
    }

    /// <summary>
    /// 确保索引可用：没有就下载；<paramref name="forceUpdate"/> 为 true 时强制重新下载。
    /// 下载进度（0-1）通过 <paramref name="progress"/> 回调。
    /// </summary>
    public async Task<WingetCatalogStatus> EnsureReadyAsync(
        bool forceUpdate = false,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceUpdate && IsReady)
            {
                return GetStatus();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
            var tempMsix = Path.Combine(Path.GetTempPath(), $"SinUpdateHub-winget-{Guid.NewGuid():N}.msix");

            try
            {
                progress?.Report(new DownloadProgress { Fraction = 0, Message = "正在下载 WinGet 索引…" });
                await DownloadAsync(tempMsix, progress, ct).ConfigureAwait(false);
                progress?.Report(new DownloadProgress { Fraction = 0.95, Message = "正在解压索引…" });
                ExtractIndex(tempMsix);
                progress?.Report(new DownloadProgress { Fraction = 1, Message = "WinGet 索引已就绪" });
                AppLog.Write($"WinGet 索引更新完成：{_indexPath}（{new FileInfo(_indexPath).Length / 1024 / 1024} MB）");
                return GetStatus();
            }
            finally
            {
                TryDelete(tempMsix);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>按关键词搜索包（包 ID / 显示名 / 规范化名称都能命中）。</summary>
    public async Task<IReadOnlyList<WingetPackageInfo>> SearchAsync(
        string keyword,
        int limit = 40,
        CancellationToken ct = default)
    {
        var kw = (keyword ?? string.Empty).Trim();
        if (kw.Length == 0)
        {
            return Array.Empty<WingetPackageInfo>();
        }

        if (!IsReady)
        {
            await EnsureReadyAsync(false, null, ct).ConfigureAwait(false);
        }

        return await Task.Run(() => SearchCore(kw, limit, ct), ct).ConfigureAwait(false);
    }

    /// <summary>取某个包 ID 的最新版本（按版本号规则比较），找不到返回 null。</summary>
    public async Task<string?> GetLatestVersionAsync(string packageId, CancellationToken ct = default)
    {
        var id = (packageId ?? string.Empty).Trim();
        if (id.Length == 0 || !IsReady)
        {
            return null;
        }

        return await Task.Run(() =>
        {
            var versions = QueryVersions(id, ct);
            return versions.Count == 0
                ? null
                : versions.OrderByDescending(v => v, Comparer<string>.Create(VersionComparer.Compare)).First();
        }, ct).ConfigureAwait(false);
    }

    // ---------------- 实现 ----------------

    private async Task DownloadAsync(string targetPath, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var response = await _factory.DownloadClient
            .GetAsync(SourceUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"下载 WinGet 索引失败（HTTP {(int)response.StatusCode}），请检查网络或代理设置");
        }

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long received = 0;
        var lastReport = DateTimeOffset.MinValue;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            if (DateTimeOffset.Now - lastReport > TimeSpan.FromMilliseconds(250))
            {
                lastReport = DateTimeOffset.Now;
                var fraction = total is > 0 ? (double)received / total.Value * 0.9 : 0;
                progress?.Report(new DownloadProgress
                {
                    Fraction = fraction,
                    Received = received,
                    Total = total,
                    Message = total is > 0
                        ? $"正在下载 WinGet 索引 {received / 1024.0 / 1024.0:0.0} / {total.Value / 1024.0 / 1024.0:0.0} MB"
                        : $"正在下载 WinGet 索引 {received / 1024.0 / 1024.0:0.0} MB",
                });
            }
        }
    }

    internal void ExtractIndex(string msixPath)
    {
        var directory = Path.GetDirectoryName(_indexPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var archive = ZipFile.OpenRead(msixPath);
        var entry = archive.GetEntry(IndexEntryName)
                    ?? archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("index.db", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("WinGet 索引包里没有找到 index.db，可能微软改了包结构");

        var tempIndex = _indexPath + ".tmp";
        try
        {
            entry.ExtractToFile(tempIndex, overwrite: true);
            _dbLock.EnterWriteLock();
            try
            {
                SqliteConnection.ClearAllPools();
                File.Move(tempIndex, _indexPath, overwrite: true);
            }
            finally
            {
                _dbLock.ExitWriteLock();
            }
        }
        catch
        {
            TryDelete(tempIndex);
            throw;
        }
    }

    private List<string> QueryVersions(string id, CancellationToken ct)
    {
        var list = new List<string>();
        _dbLock.EnterReadLock();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT v.version
                FROM manifest m
                JOIN ids i ON i.rowid = m.id
                JOIN versions v ON v.rowid = m.version
                WHERE i.id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read() && !ct.IsCancellationRequested)
            {
                list.Add(reader.GetString(0));
            }
        }
        finally
        {
            _dbLock.ExitReadLock();
        }

        return list;
    }

    private IReadOnlyList<WingetPackageInfo> SearchCore(string keyword, int limit, CancellationToken ct)
    {
        var like = "%" + EscapeLike(keyword) + "%";
        var rows = new List<(string Id, string Name, string Version)>();

        _dbLock.EnterReadLock();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT i.id, n.name, v.version
                FROM manifest m
                JOIN ids i ON i.rowid = m.id
                JOIN names n ON n.rowid = m.name
                JOIN versions v ON v.rowid = m.version
                WHERE i.id LIKE $like ESCAPE '\'
                   OR n.name LIKE $like ESCAPE '\'
                LIMIT 4000
                """;
            command.Parameters.AddWithValue("$like", like);

            using var reader = command.ExecuteReader();
            while (reader.Read() && !ct.IsCancellationRequested)
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            if (rows.Count == 0 && keyword.Length >= 2)
            {
                // 再试规范化名称（忽略空格/标点/大小写），例如 "uu远程" 命中 "UU 远程"
                var norm = Normalize(keyword);
                using var cmd2 = connection.CreateCommand();
                cmd2.CommandText = """
                    SELECT i.id, n.name, v.version
                    FROM manifest m
                    JOIN ids i ON i.rowid = m.id
                    JOIN names n ON n.rowid = m.name
                    JOIN versions v ON v.rowid = m.version
                    JOIN norm_names_map nm ON nm.manifest = m.rowid
                    JOIN norm_names nn ON nn.rowid = nm.norm_name
                    WHERE nn.norm_name LIKE $like ESCAPE '\'
                    LIMIT 4000
                    """;
                cmd2.Parameters.AddWithValue("$like", "%" + EscapeLike(norm) + "%");
                using var reader2 = cmd2.ExecuteReader();
                while (reader2.Read() && !ct.IsCancellationRequested)
                {
                    rows.Add((reader2.GetString(0), reader2.GetString(1), reader2.GetString(2)));
                }
            }
        }
        finally
        {
            _dbLock.ExitReadLock();
        }

        var lowered = keyword.ToLowerInvariant();
        return rows
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var bestVersion = g.Select(r => r.Version)
                    .OrderByDescending(v => v, Comparer<string>.Create(VersionComparer.Compare))
                    .First();
                // 用与最新版本同一行的显示名（旧版本名字可能是 GameViewer 这类旧名）
                var name = g.OrderByDescending(r => r.Version, Comparer<string>.Create(VersionComparer.Compare))
                    .First().Name;

                var idLower = g.Key.ToLowerInvariant();
                var nameLower = (name ?? string.Empty).ToLowerInvariant();
                var score = 0;
                if (idLower == lowered) score += 1000;
                if (nameLower == lowered) score += 900;
                if (idLower.Contains(lowered)) score += 300;
                if (nameLower.Contains(lowered)) score += 250;
                if (idLower.StartsWith(lowered, StringComparison.Ordinal)) score += 150;
                if (nameLower.StartsWith(lowered, StringComparison.Ordinal)) score += 120;
                score += Math.Min(bestVersion.Length, 10); // 稳定排序的轻微偏好

                return new WingetPackageInfo { Id = g.Key, Name = name ?? string.Empty, Version = bestVersion, Score = score };
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private SqliteConnection Open()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _indexPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static string EscapeLike(string text)
        => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string Normalize(string text)
    {
        var chars = text.Where(c => char.IsLetterOrDigit(c)).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }
}
