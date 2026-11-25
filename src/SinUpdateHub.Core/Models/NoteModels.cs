using System.Text.Json.Serialization;

namespace SinUpdateHub.Core.Models;

/// <summary>笔记页面内的内容块类型。</summary>
public enum NoteBlockKind
{
    /// <summary>纯文本内容块。</summary>
    Text = 0,

    /// <summary>表格块。</summary>
    Table = 1,

    /// <summary>粘贴或插入的图片块（Base64 内嵌存储）。</summary>
    Image = 2,
}

/// <summary>笔记分区（一级目录）。</summary>
public sealed class NoteSection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "新分区";

    public string ColorHex { get; set; } = "#2F75B5";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public List<NotePage> Pages { get; set; } = new();

    /// <summary>深拷贝当前分区及其所有页。</summary>
    public NoteSection Clone() => new()
    {
        Id = Id,
        Name = Name,
        ColorHex = ColorHex,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Pages = (Pages ?? new List<NotePage>()).Where(p => p is not null).Select(p => p.Clone()).ToList(),
    };

    /// <summary>规范化空值与子页结构，避免损坏或旧格式反序列化后出现空引用。</summary>
    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? "新分区" : Name.Trim();
        ColorHex = string.IsNullOrWhiteSpace(ColorHex) ? "#2F75B5" : ColorHex.Trim();
        Pages = (Pages ?? new List<NotePage>()).Where(p => p is not null).ToList();
        foreach (var page in Pages)
        {
            page.Normalize();
        }
    }

    /// <summary>过滤空值并逐个规范化分区列表。</summary>
    public static List<NoteSection> NormalizeList(IEnumerable<NoteSection?>? sections)
    {
        var result = (sections ?? Array.Empty<NoteSection?>())
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
        foreach (var section in result)
        {
            section.Normalize();
        }

        return result;
    }

    /// <summary>深拷贝并规范化整个分区列表。</summary>
    public static List<NoteSection> CloneList(IEnumerable<NoteSection?>? sections)
        => (sections ?? Array.Empty<NoteSection?>())
            .Where(s => s is not null)
            .Select(s =>
            {
                var clone = s!.Clone();
                clone.Normalize();
                return clone;
            })
            .ToList();
}

/// <summary>笔记页外置图片资源（存储在 NotePage.Images 中，正文仅引用短链接，随 data.json 与 WebDAV 同步备份）。</summary>
public sealed class NoteImageAsset
{
    public string MimeType { get; set; } = "image/png";

    public string Base64 { get; set; } = string.Empty;

    public NoteImageAsset Clone() => new()
    {
        MimeType = MimeType,
        Base64 = Base64,
    };
}

/// <summary>笔记页（二级目录，属于某个分区）。</summary>
public sealed class NotePage
{
    public const string ImageVirtualHostPrefix = "https://sinnote-img.local/";

    public const string Default3x3MarkdownTable =
        "| 列1 | 列2 | 列3 |\n| --- | --- | --- |\n|  |  |  |\n|  |  |  |";

    private static readonly System.Text.RegularExpressions.Regex InlineDataUriRegex = new(
        @"data:image/(?<subType>[a-zA-Z0-9.+-]+);base64,(?<base64>[A-Za-z0-9+/=\r\n]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex CodeFenceLineRegex = new(
        @"^\s*(`{3,}|~{3,})[^`~]*$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex TableSeparatorLineRegex = new(
        @"^(?=.*-)[\s|:\-]+$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex LongSpacesRegex = new(
        @"\s{4,}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex LongHyphensRegex = new(
        @"-{4,}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex VirtualImageKeyRegex = new(
        @"https://sinnote-img\.local/(?<key>[A-Za-z0-9_.-]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex MarkdownImageRegex = new(
        @"!\[([^\]]*)\]\([^\)]*\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex MarkdownLinkRegex = new(
        @"\[([^\]]+)\]\([^\)]*\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex HeadingPrefixRegex = new(
        @"^\s{0,3}#{1,6}\s+",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex BlockquotePrefixRegex = new(
        @"^\s*>\s?",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex InlineStyleHtmlTagRegex = new(
        @"</?(mark|span|div)\b[^>]*>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "无标题页";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Typora 风格 Markdown 正文（为空时首次读取会自动由旧版 Blocks 迁移生成）。</summary>
    public string? Markdown { get; set; }

    public string TableHeaderBgColor { get; set; } = NoteTable.DefaultHeaderBgColor;

    public string TableFirstColumnBgColor { get; set; } = NoteTable.DefaultFirstColumnBgColor;

    public string TableCellBgColor { get; set; } = NoteTable.DefaultCellBgColor;

    /// <summary>页内引用的外置图片集合（键为图片文件名如 img_xxx.png，正文只保存短虚拟链接以避免卡顿）。</summary>
    public Dictionary<string, NoteImageAsset> Images { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<NoteBlock> Blocks { get; set; } = new();

    /// <summary>深拷贝当前页及其所有内容。</summary>
    public NotePage Clone()
    {
        var copyImages = new Dictionary<string, NoteImageAsset>(StringComparer.OrdinalIgnoreCase);
        if (Images is not null)
        {
            foreach (var kv in Images)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null)
                {
                    copyImages[kv.Key] = kv.Value.Clone();
                }
            }
        }

        return new NotePage
        {
            Id = Id,
            Title = Title,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Markdown = Markdown,
            TableHeaderBgColor = TableHeaderBgColor,
            TableFirstColumnBgColor = TableFirstColumnBgColor,
            TableCellBgColor = TableCellBgColor,
            Images = copyImages,
            Blocks = (Blocks ?? new List<NoteBlock>()).Where(b => b is not null).Select(b => b.Clone()).ToList(),
        };
    }

    /// <summary>规范化空值、图片字典、表格默认配色与兼容块列表。</summary>
    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Title = Title is null ? "无标题页" : Title;
        TableHeaderBgColor = string.IsNullOrWhiteSpace(TableHeaderBgColor) ? NoteTable.DefaultHeaderBgColor : TableHeaderBgColor.Trim();
        TableFirstColumnBgColor = string.IsNullOrWhiteSpace(TableFirstColumnBgColor) ? NoteTable.DefaultFirstColumnBgColor : TableFirstColumnBgColor.Trim();
        TableCellBgColor = string.IsNullOrWhiteSpace(TableCellBgColor) ? NoteTable.DefaultCellBgColor : TableCellBgColor.Trim();

        var normalizedImages = new Dictionary<string, NoteImageAsset>(StringComparer.OrdinalIgnoreCase);
        if (Images is not null)
        {
            foreach (var kv in Images)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key)
                    && kv.Value is not null
                    && !string.IsNullOrWhiteSpace(kv.Value.Base64))
                {
                    normalizedImages[kv.Key.Trim()] = new NoteImageAsset
                    {
                        MimeType = string.IsNullOrWhiteSpace(kv.Value.MimeType) ? "image/png" : kv.Value.MimeType.Trim(),
                        Base64 = kv.Value.Base64.Trim(),
                    };
                }
            }
        }

        Images = normalizedImages;
        Blocks = (Blocks ?? new List<NoteBlock>()).Where(b => b is not null).ToList();
        foreach (var block in Blocks)
        {
            block.Normalize();
        }
    }

    /// <summary>获取有效 Markdown 内容；自动迁移旧版 Blocks 并将内嵌 Base64 图片抽取为短链接资源。</summary>
    public string EnsureMarkdown()
    {
        if (Markdown is null)
        {
            if (Blocks is not null && Blocks.Count > 0)
            {
                var firstTable = Blocks.FirstOrDefault(b => b.Kind == NoteBlockKind.Table && b.Table is not null)?.Table;
                if (firstTable is not null)
                {
                    if (!string.IsNullOrWhiteSpace(firstTable.HeaderBgColor))
                    {
                        TableHeaderBgColor = firstTable.HeaderBgColor.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(firstTable.FirstColumnBgColor))
                    {
                        TableFirstColumnBgColor = firstTable.FirstColumnBgColor.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(firstTable.CellBgColor))
                    {
                        TableCellBgColor = firstTable.CellBgColor.Trim();
                    }
                }

                Markdown = ConvertBlocksToMarkdown(Blocks);
            }
            else
            {
                Markdown = string.Empty;
            }
        }

        ExtractInlineDataUrisToAssets();
        return Markdown;
    }

    /// <summary>将 Markdown 中内嵌的超长 data:image Base64 提取至 Images 字典，正文替换为短虚拟链接并收敛旧表格超长填充空白。</summary>
    public bool ExtractInlineDataUrisToAssets()
    {
        if (string.IsNullOrEmpty(Markdown))
        {
            return false;
        }

        Images ??= new Dictionary<string, NoteImageAsset>(StringComparer.OrdinalIgnoreCase);

        var extracted = false;
        var nextMarkdown = Markdown;

        if (nextMarkdown.Contains("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            nextMarkdown = InlineDataUriRegex.Replace(nextMarkdown, match =>
            {
                var sub = match.Groups["subType"].Value;
                var rawB64 = match.Groups["base64"].Value;
                var cleanB64 = rawB64.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
                if (cleanB64.Length == 0)
                {
                    return match.Value;
                }

                extracted = true;
                var ext = ExtensionFromMimeSubType(sub);
                var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cleanB64));
                var hashHex = Convert.ToHexString(hashBytes)[..12].ToLowerInvariant();
                var key = $"img_{hashHex}{ext}";
                Images[key] = new NoteImageAsset
                {
                    MimeType = $"image/{sub.ToLowerInvariant()}",
                    Base64 = cleanB64,
                };
                return ImageVirtualHostPrefix + key;
            });
        }

        // 若抽取了旧内嵌图或存在超长 Lute 对齐填充的表格行，自动收敛多余空格和长分隔线，避免渲染卡死
        var compacted = false;
        if (extracted || nextMarkdown.IndexOf("    |", StringComparison.Ordinal) >= 0 || nextMarkdown.IndexOf("----", StringComparison.Ordinal) >= 0)
        {
            var lines = nextMarkdown.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if ((extracted || lines[i].Length > 1000) && trimmed.StartsWith('|') && trimmed.EndsWith('|'))
                {
                    var normalizedLine = LongSpacesRegex.Replace(trimmed, " ");
                    normalizedLine = LongHyphensRegex.Replace(normalizedLine, "---");
                    if (!string.Equals(lines[i], normalizedLine, StringComparison.Ordinal))
                    {
                        lines[i] = normalizedLine;
                        compacted = true;
                    }
                }
            }

            if (compacted)
            {
                nextMarkdown = string.Join("\n", lines);
            }
        }

        if (!extracted && !compacted)
        {
            return false;
        }

        Markdown = nextMarkdown;
        return true;
    }

    /// <summary>清理当前页正文不再引用的孤儿图片资源（仅建议在全量重载或备份前调用，避免影响编辑中 Ctrl+Z 撤销）。</summary>
    public bool PruneUnreferencedImages()
    {
        if (Images is null || Images.Count == 0)
        {
            return false;
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(Markdown))
        {
            foreach (System.Text.RegularExpressions.Match m in VirtualImageKeyRegex.Matches(Markdown))
            {
                var k = m.Groups["key"].Value;
                if (!string.IsNullOrWhiteSpace(k))
                {
                    referenced.Add(k);
                }
            }
        }

        var toRemove = Images.Keys.Where(k => !referenced.Contains(k)).ToList();
        if (toRemove.Count == 0)
        {
            return false;
        }

        foreach (var k in toRemove)
        {
            Images.Remove(k);
        }

        return true;
    }

    private static string ExtensionFromMimeSubType(string subType) => subType.ToLowerInvariant() switch
    {
        "jpeg" or "jpg" => ".jpg",
        "gif" => ".gif",
        "webp" => ".webp",
        "bmp" => ".bmp",
        "svg+xml" => ".svg",
        _ => ".png",
    };

    /// <summary>将旧版文本 / 表格 / 图片内容块列表转换为标准 GFM Markdown 字符串。</summary>
    public static string ConvertBlocksToMarkdown(IReadOnlyList<NoteBlock>? blocks)
    {
        if (blocks is null || blocks.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(blocks.Count);
        foreach (var block in blocks)
        {
            if (block is null)
            {
                continue;
            }

            switch (block.Kind)
            {
                case NoteBlockKind.Text:
                    if (!string.IsNullOrWhiteSpace(block.Text))
                    {
                        parts.Add(block.Text.Replace("\r\n", "\n").TrimEnd('\n'));
                    }

                    break;

                case NoteBlockKind.Table:
                    if (block.Table is not null)
                    {
                        var tableCopy = block.Table.Clone();
                        parts.Add(FormatTableAsMarkdown(tableCopy));
                    }

                    break;

                case NoteBlockKind.Image:
                    if (!string.IsNullOrWhiteSpace(block.ImageBase64))
                    {
                        var mime = string.IsNullOrWhiteSpace(block.ImageMimeType)
                            ? "image/png"
                            : block.ImageMimeType.Trim();
                        var caption = string.IsNullOrWhiteSpace(block.ImageCaption)
                            ? "image"
                            : block.ImageCaption
                                .Replace("[", string.Empty)
                                .Replace("]", string.Empty)
                                .Replace("\r", " ")
                                .Replace("\n", " ")
                                .Trim();
                        parts.Add($"![{caption}](data:{mime};base64,{block.ImageBase64.Trim()})");
                    }

                    break;
            }
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>剥离内嵌图片的 Base64 Data URI，避免全文检索匹配到编码噪声。</summary>
    public static string StripDataUriImagesForSearch(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        return InlineDataUriRegex.Replace(markdown, "data:image/inline");
    }

    /// <summary>提取供全文检索与摘要预览的可见纯文本（过滤代码围栏标识、表格分隔行、图片 Data URI 等不可见语法）。</summary>
    public static string ToSearchablePlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var cleaned = InlineDataUriRegex.Replace(markdown, string.Empty);
        cleaned = MarkdownImageRegex.Replace(cleaned, "$1");
        cleaned = MarkdownLinkRegex.Replace(cleaned, "$1");

        var lines = cleaned.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder(cleaned.Length);
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (CodeFenceLineRegex.IsMatch(line) || TableSeparatorLineRegex.IsMatch(line))
            {
                continue;
            }

            line = HeadingPrefixRegex.Replace(line, string.Empty);
            line = BlockquotePrefixRegex.Replace(line, string.Empty);
            line = InlineStyleHtmlTagRegex.Replace(line, string.Empty);
            line = line
                .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("<br />", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("\\|", "|")
                .Replace("**", string.Empty)
                .Replace("~~", string.Empty);

            // 若是表格数据行，将列竖线整理成空格，提升搜索预览可读性
            var trimmed = line.Trim();
            if (trimmed.StartsWith('|') && trimmed.EndsWith('|') && trimmed.Length >= 2)
            {
                line = string.Join(" ", trimmed[1..^1].Split('|').Select(c => c.Trim()).Where(c => c.Length > 0));
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line.Trim());
        }

        return sb.ToString();
    }

    private static string FormatTableAsMarkdown(NoteTable table)
    {
        table.Normalize();
        var colCount = Math.Max(1, table.ColumnCount);
        var sb = new System.Text.StringBuilder();

        string FormatRow(IReadOnlyList<string> row)
        {
            var cells = new string[colCount];
            for (var c = 0; c < colCount; c++)
            {
                var raw = c < row.Count ? (row[c] ?? string.Empty) : string.Empty;
                cells[c] = raw
                    .Replace("\r\n", "<br>")
                    .Replace("\n", "<br>")
                    .Replace("\r", "<br>")
                    .Replace("|", "\\|");
            }

            return "| " + string.Join(" | ", cells) + " |";
        }

        sb.AppendLine(FormatRow(table.Rows[0]));
        sb.AppendLine("| " + string.Join(" | ", Enumerable.Repeat("---", colCount)) + " |");
        for (var r = 1; r < table.Rows.Count; r++)
        {
            sb.AppendLine(FormatRow(table.Rows[r]));
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }
}

/// <summary>笔记页中的一个内容块（文本 / 表格 / 图片）。</summary>
public sealed class NoteBlock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public NoteBlockKind Kind { get; set; } = NoteBlockKind.Text;

    public string Text { get; set; } = string.Empty;

    public NoteTable? Table { get; set; }

    public string? ImageBase64 { get; set; }

    public string? ImageMimeType { get; set; }

    public string? ImageCaption { get; set; }

    /// <summary>深拷贝当前内容块。</summary>
    public NoteBlock Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Text = Text,
        Table = Table?.Clone(),
        ImageBase64 = ImageBase64,
        ImageMimeType = ImageMimeType,
        ImageCaption = ImageCaption,
    };

    /// <summary>规范化内容块字段；表格块若缺失表格实例则补默认 3×3 表格。</summary>
    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Text ??= string.Empty;
        if (Kind == NoteBlockKind.Table)
        {
            Table ??= NoteTable.CreateDefault3x3();
            Table.Normalize();
        }
    }
}

/// <summary>笔记表格数据模型（支持首行/首列/正文自定义背景色与动态增删行列）。</summary>
public sealed class NoteTable
{
    public const string DefaultHeaderBgColor = "#2F75B5";
    public const string DefaultFirstColumnBgColor = "#2F75B5";
    public const string DefaultCellBgColor = "#1E2D3D";

    public List<List<string>> Rows { get; set; } = new();

    public string HeaderBgColor { get; set; } = DefaultHeaderBgColor;

    public string FirstColumnBgColor { get; set; } = DefaultFirstColumnBgColor;

    public string CellBgColor { get; set; } = DefaultCellBgColor;

    [JsonIgnore]
    public int RowCount => Rows.Count;

    [JsonIgnore]
    public int ColumnCount => Rows.Count > 0 ? Rows[0].Count : 0;

    /// <summary>新建默认 3×3 空表格。</summary>
    public static NoteTable CreateDefault3x3()
    {
        var table = new NoteTable
        {
            HeaderBgColor = DefaultHeaderBgColor,
            FirstColumnBgColor = DefaultFirstColumnBgColor,
            CellBgColor = DefaultCellBgColor,
            Rows = new List<List<string>>(3),
        };

        for (var r = 0; r < 3; r++)
        {
            table.Rows.Add(new List<string> { string.Empty, string.Empty, string.Empty });
        }

        return table;
    }

    /// <summary>保证至少 1×1、每行列数一致、无 null 单元格，且颜色值合法。</summary>
    public void Normalize()
    {
        HeaderBgColor = string.IsNullOrWhiteSpace(HeaderBgColor) ? DefaultHeaderBgColor : HeaderBgColor.Trim();
        FirstColumnBgColor = string.IsNullOrWhiteSpace(FirstColumnBgColor) ? DefaultFirstColumnBgColor : FirstColumnBgColor.Trim();
        CellBgColor = string.IsNullOrWhiteSpace(CellBgColor) ? DefaultCellBgColor : CellBgColor.Trim();

        Rows = (Rows ?? new List<List<string>>()).Where(r => r is not null).ToList();
        if (Rows.Count == 0)
        {
            Rows.Add(new List<string> { string.Empty });
        }

        var maxCols = Rows.Max(r => r.Count);
        if (maxCols <= 0)
        {
            maxCols = 1;
        }

        foreach (var row in Rows)
        {
            for (var c = 0; c < row.Count; c++)
            {
                row[c] ??= string.Empty;
            }

            while (row.Count < maxCols)
            {
                row.Add(string.Empty);
            }
        }
    }

    /// <summary>在指定行之后插入新行（insertAfterIndex &lt; 0 或越界则追加到末尾），返回新行索引。</summary>
    public int AddRow(int insertAfterIndex = -1)
    {
        Normalize();
        var cols = Math.Max(1, ColumnCount);
        var newRow = Enumerable.Repeat(string.Empty, cols).ToList();

        var insertAt = insertAfterIndex >= 0 && insertAfterIndex < Rows.Count
            ? insertAfterIndex + 1
            : Rows.Count;
        Rows.Insert(insertAt, newRow);
        return insertAt;
    }

    /// <summary>删除指定行（rowIndex &lt; 0 或越界则删除最后一行）；至少保留 1 行。</summary>
    public bool DeleteRow(int rowIndex = -1)
    {
        Normalize();
        if (Rows.Count <= 1)
        {
            return false;
        }

        var removeAt = rowIndex >= 0 && rowIndex < Rows.Count ? rowIndex : Rows.Count - 1;
        Rows.RemoveAt(removeAt);
        return true;
    }

    /// <summary>在指定列之后插入新列（insertAfterIndex &lt; 0 或越界则追加到末尾），返回新列索引。</summary>
    public int AddColumn(int insertAfterIndex = -1)
    {
        Normalize();
        var cols = ColumnCount;
        var insertAt = insertAfterIndex >= 0 && insertAfterIndex < cols
            ? insertAfterIndex + 1
            : cols;

        foreach (var row in Rows)
        {
            row.Insert(insertAt, string.Empty);
        }

        return insertAt;
    }

    /// <summary>删除指定列（colIndex &lt; 0 或越界则删除最后一列）；至少保留 1 列。</summary>
    public bool DeleteColumn(int colIndex = -1)
    {
        Normalize();
        var cols = ColumnCount;
        if (cols <= 1)
        {
            return false;
        }

        var removeAt = colIndex >= 0 && colIndex < cols ? colIndex : cols - 1;
        foreach (var row in Rows)
        {
            if (removeAt < row.Count)
            {
                row.RemoveAt(removeAt);
            }
        }

        return true;
    }

    /// <summary>调整行顺序：将 fromIndex 行移动到 toIndex 位置。</summary>
    public bool MoveRow(int fromIndex, int toIndex)
    {
        Normalize();
        if (fromIndex < 0 || fromIndex >= Rows.Count
            || toIndex < 0 || toIndex >= Rows.Count
            || fromIndex == toIndex)
        {
            return false;
        }

        var moved = Rows[fromIndex];
        Rows.RemoveAt(fromIndex);
        Rows.Insert(toIndex, moved);
        return true;
    }

    /// <summary>在指定行之后插入带内容的新行（若 values 列数大于当前列数会自动扩展列）。</summary>
    public int InsertRowWithValues(int insertAfterIndex, IReadOnlyList<string> values)
    {
        Normalize();
        var requiredCols = Math.Max(ColumnCount, Math.Max(1, values.Count));
        foreach (var existing in Rows)
        {
            while (existing.Count < requiredCols)
            {
                existing.Add(string.Empty);
            }
        }

        var newRow = new List<string>(requiredCols);
        for (var c = 0; c < requiredCols; c++)
        {
            newRow.Add(c < values.Count ? (values[c] ?? string.Empty) : string.Empty);
        }

        var insertAt = insertAfterIndex >= 0 && insertAfterIndex < Rows.Count
            ? insertAfterIndex + 1
            : Rows.Count;
        Rows.Insert(insertAt, newRow);
        return insertAt;
    }

    /// <summary>在指定列之后插入带内容的新列（若 values 行数大于当前行数会自动扩展行）。</summary>
    public int InsertColumnWithValues(int insertAfterIndex, IReadOnlyList<string> values)
    {
        Normalize();
        var cols = Math.Max(1, ColumnCount);
        while (Rows.Count < values.Count)
        {
            Rows.Add(Enumerable.Repeat(string.Empty, cols).ToList());
        }

        var insertAt = insertAfterIndex >= 0 && insertAfterIndex < cols
            ? insertAfterIndex + 1
            : cols;

        for (var r = 0; r < Rows.Count; r++)
        {
            Rows[r].Insert(insertAt, r < values.Count ? (values[r] ?? string.Empty) : string.Empty);
        }

        return insertAt;
    }

    /// <summary>从指定起始单元格起粘贴二维网格文本（按需自动向后追加行列）。</summary>
    public void PasteGridAt(int startRow, int startCol, IReadOnlyList<IReadOnlyList<string>> grid)
    {
        Normalize();
        if (grid.Count == 0)
        {
            return;
        }

        startRow = Math.Max(0, startRow);
        startCol = Math.Max(0, startCol);
        var maxGridCols = Math.Max(1, grid.Max(r => r.Count));
        var targetRows = startRow + grid.Count;
        var targetCols = Math.Max(ColumnCount, startCol + maxGridCols);

        foreach (var existing in Rows)
        {
            while (existing.Count < targetCols)
            {
                existing.Add(string.Empty);
            }
        }

        while (Rows.Count < targetRows)
        {
            Rows.Add(Enumerable.Repeat(string.Empty, targetCols).ToList());
        }

        for (var r = 0; r < grid.Count; r++)
        {
            var srcRow = grid[r];
            for (var c = 0; c < srcRow.Count; c++)
            {
                Rows[startRow + r][startCol + c] = srcRow[c] ?? string.Empty;
            }
        }
    }

    /// <summary>提取矩形单元格区域 [minR..maxR, minC..maxC] 的内容副本。</summary>
    public List<List<string>> ExtractRange(int minRow, int maxRow, int minCol, int maxCol)
    {
        Normalize();
        var r0 = Math.Clamp(minRow, 0, Rows.Count - 1);
        var r1 = Math.Clamp(maxRow, r0, Rows.Count - 1);
        var c0 = Math.Clamp(minCol, 0, Math.Max(0, ColumnCount - 1));
        var c1 = Math.Clamp(maxCol, c0, Math.Max(0, ColumnCount - 1));

        var result = new List<List<string>>(r1 - r0 + 1);
        for (var r = r0; r <= r1; r++)
        {
            var row = new List<string>(c1 - c0 + 1);
            for (var c = c0; c <= c1; c++)
            {
                row.Add(Rows[r][c] ?? string.Empty);
            }

            result.Add(row);
        }

        return result;
    }

    /// <summary>清空矩形单元格区域 [minR..maxR, minC..maxC] 的文本内容。</summary>
    public bool ClearRange(int minRow, int maxRow, int minCol, int maxCol)
    {
        Normalize();
        var r0 = Math.Clamp(minRow, 0, Rows.Count - 1);
        var r1 = Math.Clamp(maxRow, r0, Rows.Count - 1);
        var c0 = Math.Clamp(minCol, 0, Math.Max(0, ColumnCount - 1));
        var c1 = Math.Clamp(maxCol, c0, Math.Max(0, ColumnCount - 1));

        var changed = false;
        for (var r = r0; r <= r1; r++)
        {
            for (var c = c0; c <= c1; c++)
            {
                if (!string.IsNullOrEmpty(Rows[r][c]))
                {
                    Rows[r][c] = string.Empty;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>将行内单元格列表编码为兼容 Excel / OneNote 的 TSV 字符串。</summary>
    public static string FormatTsv(IEnumerable<IReadOnlyList<string>> rows)
    {
        return string.Join(Environment.NewLine, rows.Select(FormatTsvRow));
    }

    /// <summary>将单行单元格编码为 TSV 文本。</summary>
    public static string FormatTsvRow(IReadOnlyList<string> row)
        => string.Join("\t", row.Select(EscapeTsvCell));

    /// <summary>解析制表符（\t）与换行符（\r\n / \n）分隔的表格文本，兼容带双引号的多行单元格。</summary>
    public static List<List<string>> ParseTsv(string? text)
    {
        var result = new List<List<string>>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var inQuotes = false;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                cell.Append(ch);
                i++;
                continue;
            }

            if (ch == '"' && cell.Length == 0)
            {
                inQuotes = true;
                i++;
                continue;
            }

            if (ch == '\t')
            {
                row.Add(cell.ToString());
                cell.Clear();
                i++;
                continue;
            }

            if (ch == '\r' || ch == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                result.Add(row);
                row = new List<string>();
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i += 2;
                }
                else
                {
                    i++;
                }

                continue;
            }

            cell.Append(ch);
            i++;
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            result.Add(row);
        }

        return result;
    }

    private static string EscapeTsvCell(string? value)
    {
        var s = value ?? string.Empty;
        if (s.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) < 0)
        {
            return s;
        }

        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>深拷贝当前表格。</summary>
    public NoteTable Clone()
    {
        var copy = new NoteTable
        {
            HeaderBgColor = HeaderBgColor,
            FirstColumnBgColor = FirstColumnBgColor,
            CellBgColor = CellBgColor,
            Rows = (Rows ?? new List<List<string>>())
                .Where(r => r is not null)
                .Select(r => r.Select(cell => cell ?? string.Empty).ToList())
                .ToList(),
        };
        copy.Normalize();
        return copy;
    }
}

/// <summary>SinNote 回收站条目（支持保存被删除的单个笔记页或整个分区，默认保留 30 天）。</summary>
public sealed class NoteTrashItem
{
    public const int DefaultRetentionDays = 30;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>true 表示整分区删除，false 表示单页删除。</summary>
    public bool IsSection { get; set; }

    public DateTimeOffset DeletedAt { get; set; } = DateTimeOffset.Now;

    public string OriginalSectionId { get; set; } = string.Empty;

    public string OriginalSectionName { get; set; } = string.Empty;

    public string OriginalSectionColorHex { get; set; } = "#2F75B5";

    public NoteSection? Section { get; set; }

    public NotePage? Page { get; set; }

    public NoteTrashItem Clone() => new()
    {
        Id = Id,
        IsSection = IsSection,
        DeletedAt = DeletedAt,
        OriginalSectionId = OriginalSectionId,
        OriginalSectionName = OriginalSectionName,
        OriginalSectionColorHex = OriginalSectionColorHex,
        Section = Section?.Clone(),
        Page = Page?.Clone(),
    };

    public bool IsValid => (IsSection && Section is not null) || (!IsSection && Page is not null);

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        OriginalSectionId = (OriginalSectionId ?? string.Empty).Trim();
        OriginalSectionName = string.IsNullOrWhiteSpace(OriginalSectionName) ? "默认分区" : OriginalSectionName.Trim();
        OriginalSectionColorHex = string.IsNullOrWhiteSpace(OriginalSectionColorHex) ? "#2F75B5" : OriginalSectionColorHex.Trim();
        Section?.Normalize();
        Page?.Normalize();
    }

    public static List<NoteTrashItem> NormalizeList(IEnumerable<NoteTrashItem?>? items)
    {
        var result = new List<NoteTrashItem>();
        if (items is null)
        {
            return result;
        }

        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            item.Normalize();
            if (item.IsValid)
            {
                result.Add(item);
            }
        }

        return result;
    }

    public static List<NoteTrashItem> CloneList(IEnumerable<NoteTrashItem?>? items)
        => NormalizeList((items ?? Array.Empty<NoteTrashItem?>()).Where(i => i is not null).Select(i => i!.Clone()));

    /// <summary>清理超过保留天数的回收站条目，返回清理数量。</summary>
    public static int PruneExpired(List<NoteTrashItem>? items, int retentionDays = DefaultRetentionDays, DateTimeOffset? now = null)
    {
        if (items is null || items.Count == 0)
        {
            return 0;
        }

        var cutoff = (now ?? DateTimeOffset.Now).AddDays(-Math.Max(1, retentionDays));
        return items.RemoveAll(item => item is null || !item.IsValid || item.DeletedAt < cutoff);
    }
}
