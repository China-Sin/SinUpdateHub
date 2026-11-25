using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>选择性恢复模式：合并到当前数据或完全覆盖当前数据。</summary>
public enum RestoreMode
{
    /// <summary>合并恢复：仅用勾选项更新/新增，未勾选的现有软件和笔记原样保留。</summary>
    Merge = 0,

    /// <summary>完全覆盖：清空现有数据，仅保留本次勾选的软件和笔记页面。</summary>
    Replace = 1,
}

/// <summary>用户在选择性恢复弹窗中确认的勾选结果。</summary>
public sealed class RestoreSelectionResult
{
    public HashSet<string> SelectedEntryIds { get; init; } = new(StringComparer.Ordinal);

    public HashSet<string> SelectedNotePageIds { get; init; } = new(StringComparer.Ordinal);

    public RestoreMode Mode { get; init; } = RestoreMode.Merge;

    public bool HasAnySelection => SelectedEntryIds.Count > 0 || SelectedNotePageIds.Count > 0;
}

/// <summary>选择性恢复结果摘要（用于界面提示）。</summary>
public sealed record RestoreApplySummary(
    int RestoredAppsCount,
    int RestoredDownloadsCount,
    int RestoredNotePagesCount,
    int RestoredNoteSectionsCount,
    RestoreMode Mode);

/// <summary>将备份中的指定软件条目与 SinNote 页面合并或覆盖恢复到当前 <see cref="AppData"/>。</summary>
public static class DataMergeService
{
    public static (AppData ResultData, RestoreApplySummary Summary) Apply(
        AppData? current,
        AppData incoming,
        RestoreSelectionResult selection)
    {
        var baseCurrent = current ?? new AppData();
        var incomingEntries = incoming.Entries ?? new List<AppEntry>();
        var incomingSections = NoteSection.NormalizeList(incoming.NoteSections);

        var pickedEntries = incomingEntries
            .Where(e => selection.SelectedEntryIds.Contains(e.Id))
            .Select(e => e.Clone())
            .ToList();

        var pickedSections = new List<NoteSection>();
        var restoredPagesCount = 0;
        foreach (var sec in incomingSections)
        {
            var pages = sec.Pages
                .Where(p => selection.SelectedNotePageIds.Contains(p.Id))
                .Select(p => p.Clone())
                .ToList();
            if (pages.Count == 0)
            {
                continue;
            }

            restoredPagesCount += pages.Count;
            var clonedSec = new NoteSection
            {
                Id = sec.Id,
                Name = sec.Name,
                ColorHex = sec.ColorHex,
                CreatedAt = sec.CreatedAt,
                UpdatedAt = sec.UpdatedAt,
                Pages = pages,
            };
            pickedSections.Add(clonedSec);
        }

        var restoredApps = pickedEntries.Count(e => e.Source != SourceKind.Custom);
        var restoredDownloads = pickedEntries.Count(e => e.Source == SourceKind.Custom);

        AppData result;
        if (selection.Mode == RestoreMode.Replace)
        {
            result = new AppData
            {
                SchemaVersion = incoming.SchemaVersion > 0 ? incoming.SchemaVersion : 1,
                Entries = pickedEntries,
                NoteSections = pickedSections,
            };
        }
        else
        {
            var mergedEntries = (baseCurrent.Entries ?? new List<AppEntry>())
                .Select(e => e.Clone())
                .ToList();

            foreach (var entry in pickedEntries)
            {
                var idx = mergedEntries.FindIndex(e => string.Equals(e.Id, entry.Id, StringComparison.Ordinal));
                if (idx < 0)
                {
                    idx = mergedEntries.FindIndex(e =>
                        e.Source == entry.Source
                        && string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
                }

                if (idx >= 0)
                {
                    mergedEntries[idx] = entry;
                }
                else
                {
                    mergedEntries.Add(entry);
                }
            }

            var mergedSections = NoteSection.NormalizeList(baseCurrent.NoteSections)
                .Select(s => s.Clone())
                .ToList();

            foreach (var incomingSec in pickedSections)
            {
                var targetSec = mergedSections.FirstOrDefault(s =>
                    string.Equals(s.Id, incomingSec.Id, StringComparison.Ordinal));
                targetSec ??= mergedSections.FirstOrDefault(s =>
                    string.Equals(s.Name, incomingSec.Name, StringComparison.OrdinalIgnoreCase));

                if (targetSec is null)
                {
                    mergedSections.Add(incomingSec);
                    continue;
                }

                foreach (var page in incomingSec.Pages)
                {
                    var pIdx = targetSec.Pages.FindIndex(p => string.Equals(p.Id, page.Id, StringComparison.Ordinal));
                    if (pIdx < 0)
                    {
                        pIdx = targetSec.Pages.FindIndex(p =>
                            string.Equals(p.Title, page.Title, StringComparison.OrdinalIgnoreCase));
                    }

                    if (pIdx >= 0)
                    {
                        targetSec.Pages[pIdx] = page;
                    }
                    else
                    {
                        targetSec.Pages.Add(page);
                    }
                }

                targetSec.UpdatedAt = DateTimeOffset.Now;
            }

            result = new AppData
            {
                SchemaVersion = Math.Max(baseCurrent.SchemaVersion, incoming.SchemaVersion),
                Entries = mergedEntries,
                NoteSections = mergedSections,
            };
        }

        var summary = new RestoreApplySummary(
            RestoredAppsCount: restoredApps,
            RestoredDownloadsCount: restoredDownloads,
            RestoredNotePagesCount: restoredPagesCount,
            RestoredNoteSectionsCount: pickedSections.Count,
            Mode: selection.Mode);

        return (result, summary);
    }
}
