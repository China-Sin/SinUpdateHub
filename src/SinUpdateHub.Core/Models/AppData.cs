namespace SinUpdateHub.Core.Models;

/// <summary>本地清单数据（data.json 根对象）。</summary>
public sealed class AppData
{
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public List<AppEntry> Entries { get; set; } = new();

    public List<NoteSection> NoteSections { get; set; } = new();

    public List<NoteTrashItem> NoteTrash { get; set; } = new();
}

/// <summary>备份/导出文件（WebDAV 上传与本地导出共用同一结构）。</summary>
public sealed class BackupDocument
{
    public string App { get; set; } = "SinUpdateHub";
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
    public string? MachineName { get; set; } = Environment.MachineName;
    public List<AppEntry> Entries { get; set; } = new();
    public List<NoteSection> NoteSections { get; set; } = new();

    public static BackupDocument From(AppData data) => new()
    {
        ExportedAt = DateTimeOffset.Now,
        MachineName = Environment.MachineName,
        Entries = (data.Entries ?? new List<AppEntry>()).Where(e => e is not null).Select(e => e.Clone()).ToList(),
        NoteSections = NoteSection.CloneList(data.NoteSections),
    };

    public AppData ToAppData() => new()
    {
        UpdatedAt = DateTimeOffset.Now,
        Entries = (Entries ?? new List<AppEntry>()).Where(e => e is not null).Select(e => e.Clone()).ToList(),
        NoteSections = NoteSection.CloneList(NoteSections),
    };
}

/// <summary>WebDAV 上的一个备份文件。</summary>
public sealed class BackupFileInfo
{
    public string FileName { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    public long? Size { get; set; }
    public DateTimeOffset? LastModified { get; set; }

    public string Display =>
        LastModified is null
            ? FileName
            : $"{FileName}  ({LastModified:yyyy-MM-dd HH:mm})";
}

/// <summary>WebDAV 连接测试结果。</summary>
public sealed class WebDavCheckResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? RemoteFolder { get; set; }
}

/// <summary>备份上传结果。</summary>
public sealed class BackupUploadResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    public BackupFileInfo? File { get; set; }
    public int PrunedCount { get; set; }
}
