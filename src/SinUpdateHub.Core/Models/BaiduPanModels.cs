namespace SinUpdateHub.Core.Models;

/// <summary>百度网盘 OAuth2 令牌响应。</summary>
public sealed class BaiduTokenResult
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public int ExpiresInSeconds { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Scope { get; set; } = string.Empty;
}

/// <summary>百度网盘当前登录账号信息。</summary>
public sealed class BaiduUserInfo
{
    public long Uk { get; set; }
    public string BaiduName { get; set; } = string.Empty;
    public string NetdiskName { get; set; } = string.Empty;

    /// <summary>0=普通用户，1=普通会员，2=超级会员(SVIP)。</summary>
    public int VipType { get; set; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(NetdiskName) ? NetdiskName : BaiduName;

    public string VipLabel => VipType switch
    {
        2 => "SVIP",
        1 => "VIP",
        _ => "普通用户",
    };
}

/// <summary>百度网盘文件/文件夹条目。</summary>
public sealed class BaiduFileEntry
{
    public long FsId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDir { get; set; }
    public long Size { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
}

/// <summary>待下载的网盘文件任务（含相对本地路径）。</summary>
public sealed class BaiduDownloadTaskItem
{
    public required BaiduFileEntry File { get; init; }

    /// <summary>相对于选定本地根目录的相对路径（保留子文件夹层级）。</summary>
    public required string RelativeLocalPath { get; init; }
}

/// <summary>批量下载进度回调。</summary>
public sealed class BaiduBatchProgress
{
    public int CurrentIndex { get; init; }
    public int TotalFiles { get; init; }
    public string CurrentFileName { get; init; } = string.Empty;
    public long FileReceivedBytes { get; init; }
    public long? FileTotalBytes { get; init; }
    public double FileFraction { get; init; }
    public string Message { get; init; } = string.Empty;
}
