namespace SinUpdateHub.Core.Models;

/// <summary>WinGet 本地索引里查到的包信息（用于「按名称搜包 ID」）。</summary>
public sealed class WingetPackageInfo
{
    /// <summary>WinGet 包 ID，如 NetEase.UURemote。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名（清单里的 PackageName，可能是中文）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>最新版本（按版本号比较得出，不是字符串排序）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>匹配得分（内部排序用）。</summary>
    public int Score { get; set; }

    public string Display => string.IsNullOrWhiteSpace(Name) || string.Equals(Name, Id, StringComparison.OrdinalIgnoreCase)
        ? $"{Id}  ·  最新 {Version}"
        : $"{Name}  ·  {Id}  ·  最新 {Version}";

    public override string ToString() => Display;
}

/// <summary>本地 WinGet 索引的状态。</summary>
public sealed class WingetCatalogStatus
{
    public bool Ready { get; set; }
    public string IndexPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public string Description
    {
        get
        {
            if (!Ready)
            {
                return "本地索引未下载（约 21 MB，下载一次即可按名称搜包 ID，也能让 WinGet 检查不再消耗 GitHub API 额度）";
            }

            var size = SizeBytes > 0 ? $"{SizeBytes / 1024.0 / 1024.0:0.0} MB" : "已就绪";
            var time = UpdatedAt is null ? "未知" : UpdatedAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            return $"本地索引已就绪（{size}，更新于 {time}）";
        }
    }
}
