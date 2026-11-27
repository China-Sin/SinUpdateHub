using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace SinUpdateHub.Core.Models;

/// <summary>条目来源类型。</summary>
public enum SourceKind
{
    /// <summary>GitHub Releases：Package 为 Assets 文件名筛选字符/正则。</summary>
    GitHub = 0,

    /// <summary>WinGet：Package 为 WinGet 包 ID（查 microsoft/winget-pkgs 清单）。</summary>
    WinGet = 1,

    /// <summary>官网/手动：WinGet 和 GitHub 都没有的软件；HomepageUrl 填官网下载页，检查只刷新本机版本，「更新」= 打开官网。</summary>
    Custom = 2,

    /// <summary>百度网盘：RepoUrl 存网盘文件夹/文件路径，Package 存 dir 或 file；在「软件下载」页展示，支持一键从网盘下载或直达网盘目录。</summary>
    BaiduPan = 3,
}

public static class SourceKindExtensions
{
    /// <summary>是否属于「软件下载」页（官网/手动 + 百度网盘）。</summary>
    public static bool IsDownloadSource(this SourceKind source) => source is SourceKind.Custom or SourceKind.BaiduPan;
}

/// <summary>条目的运行期检查状态（不落盘）。</summary>
public enum UpdateState
{
    Unknown = 0,
    Checking,
    UpToDate,
    UpdateAvailable,
    Failed,
}

/// <summary>代理模式。</summary>
public enum ProxyMode
{
    /// <summary>跟随 Windows 系统代理（读注册表 Internet Settings）。</summary>
    System = 0,
    /// <summary>手动填写代理地址，如 http://127.0.0.1:7897。</summary>
    Custom = 1,
    /// <summary>不使用代理。</summary>
    None = 2,
}

/// <summary>统一的 JSON 序列化配置（清单、设置、备份、缓存共用）。</summary>
public static class JsonConfig
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}
