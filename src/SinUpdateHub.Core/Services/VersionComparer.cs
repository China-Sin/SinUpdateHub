using System.Globalization;
using System.Text.RegularExpressions;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// 版本号比较：容忍 3 段/4 段、v 前缀、-beta 后缀、中文软件常见的 "1.2.3.4567" 形式。
/// </summary>
public static class VersionComparer
{
    private static readonly Regex TokenRegex = new(@"\d+|[A-Za-z]+", RegexOptions.Compiled);

    /// <summary>去掉 v/V 前缀、空白、build 元数据（+xxx）。</summary>
    public static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var v = version.Trim();
        // 允许 "版本 1.2.3"、"Version: 1.2.3" 这类写法（仅主数字段允许 . / _，连字符段整体交给后续分支以保留 1.2.3-1.beta）
        var m = Regex.Match(v, @"\d+(?:[._]\d+)*(?:[-+][0-9A-Za-z.\-_]+)?");
        if (m.Success)
        {
            v = m.Value;
        }

        v = v.TrimStart('v', 'V').Trim();
        var plus = v.IndexOf('+');
        if (plus > 0)
        {
            v = v[..plus];
        }

        return v.Trim();
    }

    /// <summary>
    /// 比较两个版本。a &lt; b 返回负数，a == b 返回 0，a &gt; b 返回正数。
    /// 无法解析时按普通字符串比较（ordinal ignore case），保证不会抛异常。
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);

        if (na.Length == 0 && nb.Length == 0)
        {
            return 0;
        }

        if (na.Length == 0)
        {
            return -1;
        }

        if (nb.Length == 0)
        {
            return 1;
        }

        if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var (coreA, suffixA) = SplitCoreAndSuffix(na);
        var (coreB, suffixB) = SplitCoreAndSuffix(nb);
        var numsA = NumericPartsLong(coreA);
        var numsB = NumericPartsLong(coreB);

        // 有数字段时优先按数字段比较（"4.33.0.8907" vs "4.48.2.5754"）
        if (numsA.Count > 0 && numsB.Count > 0)
        {
            var len = Math.Max(numsA.Count, numsB.Count);
            for (var i = 0; i < len; i++)
            {
                var x = i < numsA.Count ? numsA[i] : 0;
                var y = i < numsB.Count ? numsB[i] : 0;
                if (x != y)
                {
                    return x < y ? -1 : 1;
                }
            }

            // 数字段相同，再看后缀：无后缀（正式版）> 带后缀（beta/rc 等）
            if (suffixA.Length == 0 && suffixB.Length > 0)
            {
                return 1;
            }

            if (suffixB.Length == 0 && suffixA.Length > 0)
            {
                return -1;
            }

            if (suffixA.Length > 0 && suffixB.Length > 0)
            {
                return CompareSuffix(suffixA, suffixB);
            }

            return 0;
        }

        return string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>candidate 是否比 current 新。</summary>
    public static bool IsNewer(string? current, string? candidate)
        => Compare(current, candidate) < 0;

    /// <summary>把版本按数字段解析，如 "1.2.3.4-beta" → [1,2,3,4]。</summary>
    public static IReadOnlyList<int> NumericParts(string? version)
    {
        var (core, _) = SplitCoreAndSuffix(Normalize(version));
        var list = new List<int>();
        foreach (Match m in TokenRegex.Matches(core))
        {
            if (int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                list.Add(value);
            }
            else
            {
                break;
            }
        }

        return list;
    }

    private static (string Core, string Suffix) SplitCoreAndSuffix(string normalized)
    {
        var idx = normalized.IndexOf('-');
        if (idx < 0 || idx == normalized.Length - 1)
        {
            return (normalized, string.Empty);
        }

        var candidateSuffix = normalized[(idx + 1)..].Trim();
        // 仅当连字符右侧包含字母（如 beta / rc.1 / alpha2）时才视为预发布后缀；
        // 纯数字连字符（如 1.2.3-1、2024-01-15）作为主版本数字段参与比较。
        return candidateSuffix.Any(char.IsLetter)
            ? (normalized[..idx], candidateSuffix)
            : (normalized, string.Empty);
    }

    private static List<long> NumericPartsLong(string core)
    {
        var list = new List<long>();
        foreach (Match m in TokenRegex.Matches(core))
        {
            if (long.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                list.Add(value);
            }
            else
            {
                break;
            }
        }

        return list;
    }

    private static int CompareSuffix(string a, string b)
    {
        var tokensA = TokenRegex.Matches(a);
        var tokensB = TokenRegex.Matches(b);
        var len = Math.Min(tokensA.Count, tokensB.Count);

        for (var i = 0; i < len; i++)
        {
            var ta = tokensA[i].Value;
            var tb = tokensB[i].Value;
            var aIsNum = long.TryParse(ta, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numA);
            var bIsNum = long.TryParse(tb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numB);

            if (aIsNum && bIsNum)
            {
                if (numA != numB)
                {
                    return numA.CompareTo(numB);
                }
            }
            else if (aIsNum != bIsNum)
            {
                // 按 SemVer 2.0 规则，纯数字标识优先级低于字母标识
                return aIsNum ? -1 : 1;
            }
            else
            {
                var cmp = string.Compare(ta, tb, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0)
                {
                    return cmp;
                }
            }
        }

        return tokensA.Count.CompareTo(tokensB.Count);
    }

    /// <summary>给界面用的简短比较结论。</summary>
    public static string Describe(string? current, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return "当前版本未知";
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "最新版本未知";
        }

        return Compare(current, candidate) switch
        {
            < 0 => $"可更新 {Normalize(current)} → {Normalize(candidate)}",
            0 => "已是最新",
            _ => "本地版本比远端更新",
        };
    }
}
