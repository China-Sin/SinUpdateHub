using System.Text.RegularExpressions;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// Assets / 安装包筛选：
/// - 普通模式：文件名"包含"筛选字符（不区分大小写），例如 Setup.exe 命中 FreeType-0.1.8-Setup.exe；
/// - 正则模式：区分用户显式开启（PackageIsRegex）或字符串本身明显是正则（以 ^ 开头、含 | 或 \d 等）。
/// 另外提供"自动挑最合适安装包"的打分逻辑（优先 exe/msi、x64、Setup 关键字，排除校验文件）。
/// </summary>
public static class AssetMatcher
{
    private static readonly string[] ChecksumExtensions =
    {
        ".sha256", ".sha1", ".sha512", ".md5", ".sig", ".asc", ".pem", ".p7s", ".blockmap", ".txt", ".json", ".yml", ".yaml",
    };

    private static readonly string[] InstallerExtensions = { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".zip", ".7z", ".rar" };

    /// <summary>判断用户填的筛选串是否应按正则解释（在用户未显式指定时使用）。</summary>
    public static bool LooksLikeRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var p = pattern.Trim();
        if (p.StartsWith('^') || p.EndsWith('$') || p.StartsWith('(') || p.StartsWith('['))
        {
            return true;
        }

        // 含正则专属转义/语法
        return p.Contains("\\d", StringComparison.Ordinal)
               || p.Contains("\\w", StringComparison.Ordinal)
               || p.Contains("\\s", StringComparison.Ordinal)
               || p.Contains(".+", StringComparison.Ordinal)
               || p.Contains(".*", StringComparison.Ordinal)
               || p.Contains('|')
               || p.Contains("[0-9]")
               || p.Contains("(?i)", StringComparison.Ordinal);
    }

    /// <summary>按筛选条件过滤候选文件。pattern 为空时返回全部。</summary>
    public static List<AssetCandidate> Filter(IEnumerable<AssetCandidate> candidates, string? pattern, bool useRegex)
    {
        var all = candidates.Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return all;
        }

        var p = pattern.Trim();

        if (useRegex || LooksLikeRegex(p))
        {
            try
            {
                var re = new Regex(p, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
                var hit = all.Where(c => re.IsMatch(c.Name)).ToList();
                if (hit.Count > 0)
                {
                    return hit;
                }

                // 正则没命中，退化为普通包含匹配，避免用户写错正则就彻底查不到
            }
            catch (ArgumentException)
            {
                // 非法正则 → 走普通包含
            }
        }

        var result = new List<AssetCandidate>();
        void AddRangeUnique(IEnumerable<AssetCandidate> items)
        {
            foreach (var item in items)
            {
                if (!result.Any(r => string.Equals(r.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(item);
                }
            }
        }

        // 1) 直接包含（例如填 "Setup" 命中 FreeType-0.1.8-Setup.exe）
        AddRangeUnique(all.Where(c => c.Name.Contains(p, StringComparison.OrdinalIgnoreCase)));

        // 2) 带扩展名的筛选词：按「扩展名 + 关键词」匹配
        //    例如填 "Setup.exe" 也能命中 FreeType-0.1.8-Setup-x64.exe / xxx-Setup.exe
        AddRangeUnique(MatchByExtensionAndKeywords(all, p));

        if (result.Count > 0)
        {
            return result;
        }

        // 3) 多个关键字用竖线或空格分隔时，任一命中即算命中（如 "x64|setup"、"x64 setup"）
        var keywords = p.Split(new[] { '|', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = all.Where(c => keywords.Length == 0 || keywords.Any(k => c.Name.Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();
        if (hits.Count > 0)
        {
            return hits;
        }

        return all.Where(c => c.Name.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>所有可能作为"包"的扩展名（用于按扩展名 + 关键词匹配）。</summary>
    private static readonly string[] FilterExtensions =
    {
        ".exe", ".msi", ".msixbundle", ".msix", ".appxbundle", ".appx", ".zip", ".7z", ".rar", ".tar.gz", ".tgz", ".apk", ".dmg", ".deb", ".rpm",
    };

    /// <summary>
    /// 「扩展名 + 关键词」匹配：把 "Setup.exe" 拆成 关键词 Setup + 扩展名 .exe，
    /// 要求文件名以该扩展名结尾且包含全部关键词（纯数字词忽略，避免 0.1.8 这类版本号误伤）。
    /// </summary>
    private static IEnumerable<AssetCandidate> MatchByExtensionAndKeywords(IEnumerable<AssetCandidate> all, string pattern)
    {
        var ext = FilterExtensions.FirstOrDefault(e => pattern.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        if (ext is null)
        {
            return Array.Empty<AssetCandidate>();
        }

        var stem = pattern[..^ext.Length];
        var tokens = Regex.Split(stem, @"[^\p{L}\p{N}]+")
            .Where(t => t.Length >= 3 && !t.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return all.Where(c =>
            c.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) &&
            (tokens.Length == 0 || tokens.All(t => c.Name.Contains(t, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>从候选里挑最像"Windows 安装包"的一个；挑不出返回 null。</summary>
    public static AssetCandidate? PickBest(IReadOnlyList<AssetCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var scored = candidates
            .Select(c => (Candidate: c, Score: Score(c.Name)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Candidate.Name.Length)
            .ToList();

        var best = scored[0];
        return best.Score <= -100 ? null : best.Candidate;
    }

    /// <summary>给文件名打分：越高越像目标安装包。</summary>
    public static int Score(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        var ext = InstallerExtensions.FirstOrDefault(e => name.EndsWith(e, StringComparison.Ordinal));
        if (ext is null)
        {
            return -200;
        }

        if (ChecksumExtensions.Any(c => name.EndsWith(c, StringComparison.Ordinal)))
        {
            return -300;
        }

        var score = ext switch
        {
            ".exe" => 100,
            ".msi" => 90,
            ".msix" or ".msixbundle" or ".appx" or ".appxbundle" => 60,
            ".zip" => 30,
            _ => 10,
        };

        if (name.Contains("setup") || name.Contains("installer") || name.Contains("install"))
        {
            score += 40;
        }

        if (name.Contains("x64") || name.Contains("amd64") || name.Contains("win64") || name.Contains("64bit"))
        {
            score += 25;
        }

        if (name.Contains("arm64"))
        {
            score -= 20;
        }

        if (name.Contains("x86") || name.Contains("win32") || name.Contains("32bit") || name.Contains("ia32"))
        {
            score -= 15;
        }

        if (name.Contains("portable") || name.Contains("green") || name.Contains("source") || name.Contains("src") || name.Contains("debug") || name.Contains("symbols"))
        {
            score -= 60;
        }

        if (name.Contains("powerShell".ToLowerInvariant()) || name.EndsWith(".ps1", StringComparison.Ordinal))
        {
            score -= 80;
        }

        if (name.Contains("latest"))
        {
            score += 5;
        }

        return score;
    }

    /// <summary>解析 GitHub 仓库地址：支持完整 URL、owner/repo、(结尾 .git)。返回 null 表示无法解析。</summary>
    public static (string Owner, string Repo)? ParseGitHubRepo(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var s = input.Trim();
        if (s.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            s = "https://" + s;
        }

        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
            (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return (parts[0], TrimGit(parts[1]));
            }

            return null;
        }

        // owner/repo 简写
        var segs = s.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 2 && !segs[0].Contains('.') && !segs[1].Contains(' '))
        {
            return (segs[0], TrimGit(segs[1]));
        }

        return null;
    }

    private static string TrimGit(string repo)
        => repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;

    /// <summary>把安装包 URL 转成可读文件名（去掉查询串）。</summary>
    public static string FileNameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var name = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? string.Empty).Trim('/');
            if (!string.IsNullOrWhiteSpace(name))
            {
                return Sanitize(name);
            }
        }

        return "download.bin";
    }

    /// <summary>
    /// 文件名是否带"安装包"扩展名。像 download.php / getfile.aspx 这种动态下载地址返回 false，
    /// 需要去问服务器真实文件名，或者按清单里的 InstallerType 补扩展名。
    /// </summary>
    public static bool HasInstallerExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = fileName.Trim().ToLowerInvariant();
        return InstallerExtensions.Any(ext => name.EndsWith(ext, StringComparison.Ordinal));
    }

    /// <summary>按 WinGet 清单的 InstallerType 给出合适的文件扩展名。</summary>
    public static string ExtensionFromInstallerType(string? installerType)
    {
        return (installerType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "msi" or "wix" => ".msi",
            "msix" => ".msix",
            "msixbundle" => ".msixbundle",
            "appx" => ".appx",
            "appxbundle" => ".appxbundle",
            "zip" or "portable" => ".zip",
            "exe" or "inno" or "nullsoft" or "burn" or "squirrel" or "mz" or "nsis" => ".exe",
            _ => ".exe",
        };
    }

    /// <summary>解析 Content-Disposition 里的真实文件名（支持 filename*=UTF-8''xxx 与普通 filename）。</summary>
    public static string? FileNameFromContentDisposition(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var star = Regex.Match(raw, @"filename\*\s*=\s*(?:UTF-8|utf-8)?''([^;]+)", RegexOptions.IgnoreCase);
        if (star.Success)
        {
            var value = star.Groups[1].Value.Trim().Trim('"');
            try
            {
                return Uri.UnescapeDataString(value);
            }
            catch
            {
                return value;
            }
        }

        var plain = Regex.Match(raw, @"filename\s*=\s*""?([^"";]+)""?", RegexOptions.IgnoreCase);
        return plain.Success ? plain.Groups[1].Value.Trim() : null;
    }

    /// <summary>解析 Content-Disposition 头对象里的真实文件名。</summary>
    public static string? FileNameFromContentDisposition(System.Net.Http.Headers.ContentDispositionHeaderValue? disposition)
    {
        if (disposition is null)
        {
            return null;
        }

        var name = disposition.FileNameStar ?? disposition.FileName;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim().Trim('"');
    }

    /// <summary>清理文件名中的非法字符。</summary>
    public static string Sanitize(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        if (s.Length == 0)
        {
            return "download.bin";
        }

        return s.Length > 180 ? s[..180] : s;
    }
}
