namespace SinUpdateHub.Core.Models;

/// <summary>下载进度。</summary>
public sealed class DownloadProgress
{
    public double Fraction { get; set; }
    public long Received { get; set; }
    public long? Total { get; set; }
    public string? Message { get; set; }
}

/// <summary>安装器执行结果。</summary>
public sealed class InstallerResult
{
    public bool Launched { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public string? CommandLine { get; set; }

    /// <summary>
    /// 需要用户手动处理（压缩包/msix 等）：软件只打开了所在目录，
    /// 此时不要等待"安装完成"，也不要自动回填版本。
    /// </summary>
    public bool NeedsManualAction { get; set; }

    /// <summary>是否已由 InstallerRunner 等待安装进程退出（非提权模式下为 true；UAC 提权直接返回时为 false）。</summary>
    public bool WaitedForExit { get; set; }
}
