using System.Text;
using System.Text.Json;
using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// 清单持久化（默认 %APPDATA%\SinUpdateHub\data.json）：
/// - 保存时先把现有文件备份为 data.json.bak（只保留一份），再写 .tmp 后原子替换；
/// - 读取失败时把坏文件复制为 data.corrupt-yyyyMMddHHmmss.json 并回退为空清单，避免启动崩溃；
/// - 支持导出 / 导入与 WebDAV 共用的 BackupDocument 结构；
/// - 内部加锁，可被后台线程（自动检查 / 备份）与 UI 线程同时调用。
/// </summary>
public sealed class AppStore : IAppStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly object _gate = new();

    /// <inheritdoc />
    public string FilePath { get; }

    /// <param name="filePath">清单路径；null/空 = <see cref="AppPaths.DataFile"/>（测试可传临时文件）。</param>
    public AppStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? AppPaths.DataFile : filePath.Trim();
    }

    /// <summary>读取清单；文件不存在、为空或损坏时返回空 <see cref="AppData"/>（损坏文件会另存备份）。</summary>
    public AppData Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    /// <summary>保存清单（先备份旧文件，再 tmp + 原子替换），并刷新 <see cref="AppData.UpdatedAt"/>。</summary>
    public void Save(AppData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            SaveCore(data);
        }
    }

    /// <summary>导出为备份 JSON（UTF-8 无 BOM、缩进），目录不存在时自动创建。</summary>
    public void Export(AppData data, string path)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            ExportCore(data, path);
        }
    }

    /// <summary>导入备份文件：兼容 <see cref="BackupDocument"/> 与 <see cref="AppData"/> 两种结构。</summary>
    public AppData Import(string path)
    {
        lock (_gate)
        {
            return ImportCore(path);
        }
    }

    private AppData LoadCore()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppData();
            }

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppData();
            }

            var data = JsonSerializer.Deserialize<AppData>(json, JsonConfig.Options);
            if (data is null)
            {
                throw new JsonException("清单内容为空");
            }

            data.Entries = (data.Entries ?? new List<AppEntry>()).Where(e => e is not null).ToList();
            data.NoteSections = NoteSection.NormalizeList(data.NoteSections);
            data.NoteTrash = NoteTrashItem.NormalizeList(data.NoteTrash);
            return data;
        }
        catch (Exception ex)
        {
            AppLog.Write($"读取清单失败，已回退为空清单：{FilePath}", ex);
            BackupCorruptFile();
            return new AppData();
        }
    }

    private void SaveCore(AppData data)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        data.UpdatedAt = DateTimeOffset.Now;
        var json = JsonSerializer.Serialize(data, JsonConfig.Options);

        if (File.Exists(FilePath))
        {
            try
            {
                File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            }
            catch (Exception ex)
            {
                // 备份失败不应阻止保存
                AppLog.Write($"备份旧清单失败（继续保存）：{FilePath}", ex);
            }
        }

        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json, Utf8NoBom);
        File.Move(tempPath, FilePath, overwrite: true);
        var notePageCount = data.NoteSections.Sum(s => s.Pages.Count);
        var noteSummary = data.NoteSections.Count > 0
            ? $"，SinNote {data.NoteSections.Count} 分区 / {notePageCount} 页"
            : string.Empty;
        AppLog.Write($"清单已保存：{FilePath}（{data.Entries.Count} 条{noteSummary}）");
    }

    private void ExportCore(AppData data, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("导出路径不能为空");
        }

        var fullPath = Path.GetFullPath(path.Trim());
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = BackupDocument.From(data);
        var json = JsonSerializer.Serialize(document, JsonConfig.Options);
        File.WriteAllText(fullPath, json, Utf8NoBom);
        AppLog.Write($"清单已导出：{fullPath}");
    }

    private AppData ImportCore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("导入路径不能为空");
        }

        var fullPath = Path.GetFullPath(path.Trim());
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"导入文件不存在：{fullPath}");
        }

        string json;
        try
        {
            json = File.ReadAllText(fullPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法读取导入文件：{fullPath}（{ex.Message}）", ex);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"导入失败：文件内容为空（{Path.GetFileName(fullPath)}）");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"导入失败：{Path.GetFileName(fullPath)} 不是有效的 JSON 对象");
            }

            if (root.TryGetProperty("app", out _) || root.TryGetProperty("exportedAt", out _))
            {
                var backup = JsonSerializer.Deserialize<BackupDocument>(json, JsonConfig.Options)
                             ?? throw new InvalidOperationException("备份内容为空");
                return backup.ToAppData();
            }

            if (!root.TryGetProperty("entries", out _))
            {
                throw new InvalidOperationException(
                    $"导入失败：{Path.GetFileName(fullPath)} 不是 SinUpdateHub 的清单或备份文件（缺少 entries 字段）");
            }

            var appData = JsonSerializer.Deserialize<AppData>(json, JsonConfig.Options)
                          ?? throw new InvalidOperationException("清单内容为空");
            appData.Entries = (appData.Entries ?? new List<AppEntry>()).Where(e => e is not null).ToList();
            appData.NoteSections = NoteSection.NormalizeList(appData.NoteSections);
            return appData;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"导入失败：{Path.GetFileName(fullPath)} 不是有效的 JSON 文件，请选择 SinUpdateHub 导出的清单或备份",
                ex);
        }
    }

    private void BackupCorruptFile()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            var name = Path.GetFileNameWithoutExtension(FilePath);
            var corruptPath = Path.Combine(directory, $"{name}.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Copy(FilePath, corruptPath, overwrite: true);
            AppLog.Write($"坏清单已另存：{corruptPath}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"保存坏清单副本失败：{FilePath}", ex);
        }
    }
}
