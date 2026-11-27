using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SinUpdateHub.App.ViewModels;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.Views;

/// <summary>选择性恢复弹窗：支持全选/全不选/单选软件条目与 SinNote 笔记页面，支持合并或覆盖恢复。</summary>
public partial class RestoreSelectionWindow : Window
{
    private readonly RestoreSelectionViewModel _vm;

    public RestoreSelectionWindow(string sourceLabel, AppData incomingData)
    {
        InitializeComponent();
        _vm = new RestoreSelectionViewModel(sourceLabel, incomingData);
        DataContext = _vm;
    }

    public RestoreSelectionResult? Result { get; private set; }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        var result = _vm.BuildResult();
        if (!result.HasAnySelection)
        {
            return;
        }

        Result = result;
        DialogResult = true;
        Close();
    }
}

internal sealed class SelectableSoftwareItem : ObservableObject
{
    private readonly Action _onSelectionChanged;
    private bool _isSelected = true;

    public SelectableSoftwareItem(AppEntry entry, Action onSelectionChanged)
    {
        Entry = entry;
        _onSelectionChanged = onSelectionChanged;
    }

    public AppEntry Entry { get; }

    public string EntryId => Entry.Id;

    public string DisplayName => !string.IsNullOrWhiteSpace(Entry.DisplayName)
        ? Entry.DisplayName
        : (string.IsNullOrWhiteSpace(Entry.Name) ? "(未命名)" : Entry.Name);

    public SourceKind Source => Entry.Source;

    public string SourceText => Entry.Source switch
    {
        SourceKind.GitHub => "GitHub",
        SourceKind.WinGet => "WinGet",
        _ => "官网",
    };

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Entry.Package))
            {
                parts.Add(Entry.Package);
            }
            else if (!string.IsNullOrWhiteSpace(Entry.RepoUrl))
            {
                parts.Add(Entry.RepoUrl);
            }
            else if (!string.IsNullOrWhiteSpace(Entry.HomepageUrl))
            {
                parts.Add(Entry.HomepageUrl);
            }

            if (!string.IsNullOrWhiteSpace(Entry.CurrentVersion))
            {
                parts.Add($"v{Entry.CurrentVersion}");
            }

            return parts.Count == 0 ? (Entry.Source.IsDownloadSource() ? "软件下载条目" : "软件列表条目") : string.Join(" · ", parts);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _onSelectionChanged();
            }
        }
    }

    public void SetSelectedSilently(bool selected)
    {
        SetProperty(ref _isSelected, selected, nameof(IsSelected));
    }
}

internal sealed class SelectableNotePageItem : ObservableObject
{
    private readonly Action _onSelectionChanged;
    private bool _isSelected = true;

    public SelectableNotePageItem(NotePage page, Action onSelectionChanged)
    {
        Page = page;
        _onSelectionChanged = onSelectionChanged;
    }

    public NotePage Page { get; }

    public string PageId => Page.Id;

    public string PageTitle => string.IsNullOrWhiteSpace(Page.Title) ? "无标题页" : Page.Title;

    public string UpdatedText => Page.UpdatedAt.LocalDateTime.ToString("MM-dd HH:mm");

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _onSelectionChanged();
            }
        }
    }

    public void SetSelectedSilently(bool selected)
    {
        SetProperty(ref _isSelected, selected, nameof(IsSelected));
    }
}

internal sealed class SelectableNoteSectionGroup : ObservableObject
{
    private readonly Action _onSelectionChanged;
    private bool _isAllPagesSelected = true;

    public SelectableNoteSectionGroup(NoteSection section, Action onSelectionChanged)
    {
        Section = section;
        _onSelectionChanged = onSelectionChanged;

        Pages = new ObservableCollection<SelectableNotePageItem>(
            section.Pages.Select(p => new SelectableNotePageItem(p, OnChildSelectionChanged)));
        SyncHeaderState();
    }

    public NoteSection Section { get; }

    public string SectionName => string.IsNullOrWhiteSpace(Section.Name) ? "未命名分区" : Section.Name;

    public string ColorHex => Section.ColorHex;

    public ObservableCollection<SelectableNotePageItem> Pages { get; }

    public bool IsAllPagesSelected
    {
        get => _isAllPagesSelected;
        set
        {
            if (SetProperty(ref _isAllPagesSelected, value))
            {
                foreach (var page in Pages)
                {
                    page.SetSelectedSilently(value);
                }

                OnPropertyChanged(nameof(SelectionCountText));
                _onSelectionChanged();
            }
        }
    }

    public string SelectionCountText => $"{Pages.Count(p => p.IsSelected)}/{Pages.Count} 页";

    public void SetAllPagesSelectedSilently(bool selected)
    {
        foreach (var page in Pages)
        {
            page.SetSelectedSilently(selected);
        }

        SetProperty(ref _isAllPagesSelected, selected, nameof(IsAllPagesSelected));
        OnPropertyChanged(nameof(SelectionCountText));
    }

    private void OnChildSelectionChanged()
    {
        SyncHeaderState();
        _onSelectionChanged();
    }

    private void SyncHeaderState()
    {
        var allSelected = Pages.Count > 0 && Pages.All(p => p.IsSelected);
        SetProperty(ref _isAllPagesSelected, allSelected, nameof(IsAllPagesSelected));
        OnPropertyChanged(nameof(SelectionCountText));
    }
}

internal sealed class RestoreSelectionViewModel : ObservableObject
{
    private readonly List<SelectableSoftwareItem> _allSoftwareItems;
    private string _softwareFilterText = string.Empty;
    private bool _isMergeMode = true;

    public RestoreSelectionViewModel(string sourceLabel, AppData incomingData)
    {
        SourceDisplay = string.IsNullOrWhiteSpace(sourceLabel) ? string.Empty : $"来源：{sourceLabel}";

        var entries = incomingData.Entries ?? new List<AppEntry>();
        _allSoftwareItems = entries
            .Select(e => new SelectableSoftwareItem(e, RefreshSummaries))
            .ToList();

        FilteredSoftwareItems = new ObservableCollection<SelectableSoftwareItem>(_allSoftwareItems);

        var sections = NoteSection.NormalizeList(incomingData.NoteSections);
        NoteSectionGroups = new ObservableCollection<SelectableNoteSectionGroup>(
            sections
                .Where(s => s.Pages.Count > 0)
                .Select(s => new SelectableNoteSectionGroup(s, RefreshSummaries)));

        var appCount = entries.Count(e => !e.Source.IsDownloadSource());
        var downloadCount = entries.Count(e => e.Source.IsDownloadSource());
        var pageCount = NoteSectionGroups.Sum(g => g.Pages.Count);
        SourceSummaryText = $"备份共包含：软件列表 {appCount} 条 · 软件下载 {downloadCount} 条 · SinNote {NoteSectionGroups.Count} 分区 / {pageCount} 页";

        SelectAllCommand = new RelayCommand(() => SetAll(true, true));
        ClearAllCommand = new RelayCommand(() => SetAll(false, false));
        SelectSoftwareOnlyCommand = new RelayCommand(() => SetAll(true, false));
        SelectNotesOnlyCommand = new RelayCommand(() => SetAll(false, true));
        SelectAllSoftwareCommand = new RelayCommand(() => SetSoftware(true));
        ClearAllSoftwareCommand = new RelayCommand(() => SetSoftware(false));
        SelectAllNotesCommand = new RelayCommand(() => SetNotes(true));
        ClearAllNotesCommand = new RelayCommand(() => SetNotes(false));
    }

    public string SourceDisplay { get; }

    public string SourceSummaryText { get; }

    public ObservableCollection<SelectableSoftwareItem> FilteredSoftwareItems { get; }

    public ObservableCollection<SelectableNoteSectionGroup> NoteSectionGroups { get; }

    public bool HasNoSoftware => _allSoftwareItems.Count == 0;

    public bool HasNoNotes => NoteSectionGroups.Count == 0;

    public string SoftwareFilterText
    {
        get => _softwareFilterText;
        set
        {
            if (SetProperty(ref _softwareFilterText, value ?? string.Empty))
            {
                ApplySoftwareFilter();
            }
        }
    }

    public bool IsMergeMode
    {
        get => _isMergeMode;
        set
        {
            if (SetProperty(ref _isMergeMode, value))
            {
                OnPropertyChanged(nameof(IsReplaceMode));
            }
        }
    }

    public bool IsReplaceMode
    {
        get => !_isMergeMode;
        set
        {
            if (value == _isMergeMode)
            {
                IsMergeMode = !value;
            }
        }
    }

    public int SelectedSoftwareCount => _allSoftwareItems.Count(i => i.IsSelected);

    public int SelectedNotesCount => NoteSectionGroups.Sum(g => g.Pages.Count(p => p.IsSelected));

    public string SoftwareHeaderText => $"软件列表 / 软件下载（已选 {SelectedSoftwareCount}/{_allSoftwareItems.Count}）";

    public string NotesHeaderText => $"SinNote 笔记页（已选 {SelectedNotesCount}/{NoteSectionGroups.Sum(g => g.Pages.Count)}）";

    public string SelectionSummaryText => CanConfirm
        ? $"已选：软件 {SelectedSoftwareCount} 条 · SinNote {SelectedNotesCount} 页"
        : "请至少勾选 1 个软件条目或 1 个笔记页面";

    public bool CanConfirm => SelectedSoftwareCount > 0 || SelectedNotesCount > 0;

    public ICommand SelectAllCommand { get; }
    public ICommand ClearAllCommand { get; }
    public ICommand SelectSoftwareOnlyCommand { get; }
    public ICommand SelectNotesOnlyCommand { get; }
    public ICommand SelectAllSoftwareCommand { get; }
    public ICommand ClearAllSoftwareCommand { get; }
    public ICommand SelectAllNotesCommand { get; }
    public ICommand ClearAllNotesCommand { get; }

    public RestoreSelectionResult BuildResult()
    {
        return new RestoreSelectionResult
        {
            SelectedEntryIds = _allSoftwareItems
                .Where(i => i.IsSelected)
                .Select(i => i.EntryId)
                .ToHashSet(StringComparer.Ordinal),
            SelectedNotePageIds = NoteSectionGroups
                .SelectMany(g => g.Pages)
                .Where(p => p.IsSelected)
                .Select(p => p.PageId)
                .ToHashSet(StringComparer.Ordinal),
            Mode = IsMergeMode ? RestoreMode.Merge : RestoreMode.Replace,
        };
    }

    private void ApplySoftwareFilter()
    {
        var q = _softwareFilterText.Trim();
        FilteredSoftwareItems.Clear();
        foreach (var item in _allSoftwareItems)
        {
            if (q.Length == 0
                || item.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.SourceText.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.DetailText.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredSoftwareItems.Add(item);
            }
        }
    }

    private void SetAll(bool softwareSelected, bool notesSelected)
    {
        foreach (var item in _allSoftwareItems)
        {
            item.SetSelectedSilently(softwareSelected);
        }

        foreach (var group in NoteSectionGroups)
        {
            group.SetAllPagesSelectedSilently(notesSelected);
        }

        RefreshSummaries();
    }

    private void SetSoftware(bool selected)
    {
        foreach (var item in FilteredSoftwareItems)
        {
            item.SetSelectedSilently(selected);
        }

        RefreshSummaries();
    }

    private void SetNotes(bool selected)
    {
        foreach (var group in NoteSectionGroups)
        {
            group.SetAllPagesSelectedSilently(selected);
        }

        RefreshSummaries();
    }

    private void RefreshSummaries()
    {
        RaisePropertyChanged(
            nameof(SelectedSoftwareCount),
            nameof(SelectedNotesCount),
            nameof(SoftwareHeaderText),
            nameof(NotesHeaderText),
            nameof(SelectionSummaryText),
            nameof(CanConfirm));
    }
}
