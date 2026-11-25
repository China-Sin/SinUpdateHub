using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SinUpdateHub.App.Services;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.ViewModels;

/// <summary>调色板预设项（名称 + 十六进制颜色）。</summary>
public sealed record NoteColorPreset(string Name, string Hex);

/// <summary>编辑器字体格式预设（显示名称 + CSS font-family 栈）。</summary>
public sealed record NoteFontPreset(string Name, string CssValue);

/// <summary>SinNote 查找范围（当前页面 vs 全部分区）。</summary>
public enum NoteSearchScope
{
    CurrentPage = 0,
    AllSections = 1,
}

/// <summary>SinNote 查找结果条目。</summary>
public sealed class NoteSearchResultItem
{
    public required NoteSectionItemViewModel Section { get; init; }
    public required NotePageItemViewModel Page { get; init; }
    public bool IsTitleMatch { get; init; }
    public int MatchIndex { get; init; }
    public int MatchLength { get; init; }
    public int OccurrenceIndex { get; init; }
    public string SourceTag { get; init; } = string.Empty;
    public string Snippet { get; init; } = string.Empty;

    public string PageTitle => string.IsNullOrWhiteSpace(Page.Title) ? "无标题页" : Page.Title;

    public string SectionDisplay => $"({Section.Name})";
}

/// <summary>SinNote 回收站列表项 ViewModel。</summary>
public sealed class NoteTrashItemViewModel
{
    public NoteTrashItemViewModel(NoteTrashItem item)
    {
        Item = item;
    }

    public NoteTrashItem Item { get; }

    public string Id => Item.Id;

    public bool IsSection => Item.IsSection;

    public string TypeBadgeText => Item.IsSection ? "分区" : "笔记页";

    public string AccentColorHex => string.IsNullOrWhiteSpace(Item.OriginalSectionColorHex)
        ? "#2F75B5"
        : Item.OriginalSectionColorHex;

    public string DisplayTitle => Item.IsSection
        ? (string.IsNullOrWhiteSpace(Item.Section?.Name) ? Item.OriginalSectionName : Item.Section!.Name)
        : (string.IsNullOrWhiteSpace(Item.Page?.Title) ? "无标题页" : Item.Page!.Title);

    public string OriginInfoText => Item.IsSection
        ? $"包含 {Item.Section?.Pages.Count ?? 0} 个笔记页"
        : $"原分区：{Item.OriginalSectionName}";

    public string DeletedInfoText
    {
        get
        {
            var elapsedDays = (int)Math.Floor((DateTimeOffset.Now - Item.DeletedAt).TotalDays);
            var remainDays = Math.Max(1, NoteTrashItem.DefaultRetentionDays - Math.Max(0, elapsedDays));
            return $"删除于 {Item.DeletedAt.LocalDateTime:yyyy-MM-dd HH:mm}  ·  剩余 {remainDays} 天自动清理";
        }
    }
}

/// <summary>笔记分区列表项 ViewModel。</summary>
public sealed class NoteSectionItemViewModel : ObservableObject
{
    private readonly NotesViewModel _owner;
    private bool _isSelected;

    public NoteSectionItemViewModel(NotesViewModel owner, NoteSection section)
    {
        _owner = owner;
        Section = section;

        Pages = new ObservableCollection<NotePageItemViewModel>(
            (section.Pages ?? new List<NotePage>()).Select(p => new NotePageItemViewModel(owner, this, p)));
        Pages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PageCountText));
    }

    public NotesViewModel Owner => _owner;

    public NoteSection Section { get; }

    public string Id => Section.Id;

    public string Name
    {
        get => Section.Name;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? "新分区" : value.Trim();
            if (Section.Name == trimmed)
            {
                return;
            }

            Section.Name = trimmed;
            Section.UpdatedAt = DateTimeOffset.Now;
            OnPropertyChanged();
            _owner.ScheduleSave();
        }
    }

    public string ColorHex
    {
        get => Section.ColorHex;
        set
        {
            var normalized = NoteImageHelper.NormalizeHexOrNull(value) ?? "#2F75B5";
            if (string.Equals(Section.ColorHex, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Section.ColorHex = normalized;
            Section.UpdatedAt = DateTimeOffset.Now;
            OnPropertyChanged();
            _owner.ScheduleSave();
        }
    }

    public ObservableCollection<NotePageItemViewModel> Pages { get; }

    public string PageCountText => $"{Pages.Count} 页";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public void TouchUpdatedAt()
    {
        Section.UpdatedAt = DateTimeOffset.Now;
    }
}

/// <summary>笔记页列表项 ViewModel（基于 Typora 风格 Markdown 正文与页面表格配色）。</summary>
public sealed class NotePageItemViewModel : ObservableObject
{
    private readonly NotesViewModel _owner;
    private readonly NoteSectionItemViewModel _parentSection;
    private bool _isSelected;

    public NotePageItemViewModel(NotesViewModel owner, NoteSectionItemViewModel parentSection, NotePage page)
    {
        _owner = owner;
        _parentSection = parentSection;
        Page = page;
        Page.EnsureMarkdown();
    }

    public NotesViewModel Owner => _owner;

    public NotePage Page { get; }

    public string Id => Page.Id;

    public string Title
    {
        get => Page.Title;
        set
        {
            var next = value ?? string.Empty;
            if (Page.Title == next)
            {
                return;
            }

            Page.Title = next;
            TouchUpdatedAt();
            OnPropertyChanged();
            _owner.ScheduleSave();
        }
    }

    public string Markdown => Page.EnsureMarkdown();

    public string TableHeaderBgColor => string.IsNullOrWhiteSpace(Page.TableHeaderBgColor)
        ? NoteTable.DefaultHeaderBgColor
        : Page.TableHeaderBgColor;

    public string TableFirstColumnBgColor => string.IsNullOrWhiteSpace(Page.TableFirstColumnBgColor)
        ? NoteTable.DefaultFirstColumnBgColor
        : Page.TableFirstColumnBgColor;

    public string TableCellBgColor => string.IsNullOrWhiteSpace(Page.TableCellBgColor)
        ? NoteTable.DefaultCellBgColor
        : Page.TableCellBgColor;

    public DateTimeOffset CreatedAt => Page.CreatedAt;

    public DateTimeOffset UpdatedAt => Page.UpdatedAt;

    public string DateLineText =>
        $"创建于 {CreatedAt.LocalDateTime:yyyy年M月d日 HH:mm}  ·  更新于 {UpdatedAt.LocalDateTime:MM-dd HH:mm}";

    public string ShortDateText => UpdatedAt.LocalDateTime.ToString("MM-dd HH:mm");

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>由编辑器回写最新 Markdown 正文；仅内容变化时更新并清理已迁移的旧版 Blocks。</summary>
    public bool SetMarkdownFromEditor(string markdown)
    {
        var next = markdown ?? string.Empty;
        var current = Page.EnsureMarkdown();
        if (string.Equals(current, next, StringComparison.Ordinal))
        {
            return false;
        }

        Page.Markdown = next;
        if (Page.Blocks.Count > 0)
        {
            // 避免旧版 Blocks 内嵌图片与 Markdown 中的 data URI 重复占用存储空间
            Page.Blocks.Clear();
        }

        TouchUpdatedAt();
        OnPropertyChanged(nameof(Markdown));
        return true;
    }

    public bool ApplyTableColors(string? headerBg, string? firstColBg, string? cellBg)
    {
        var changed = false;

        if (!string.IsNullOrWhiteSpace(headerBg))
        {
            var normalized = NoteImageHelper.NormalizeHexOrNull(headerBg);
            if (normalized is not null && !string.Equals(Page.TableHeaderBgColor, normalized, StringComparison.OrdinalIgnoreCase))
            {
                Page.TableHeaderBgColor = normalized;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(firstColBg))
        {
            var normalized = NoteImageHelper.NormalizeHexOrNull(firstColBg);
            if (normalized is not null && !string.Equals(Page.TableFirstColumnBgColor, normalized, StringComparison.OrdinalIgnoreCase))
            {
                Page.TableFirstColumnBgColor = normalized;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(cellBg))
        {
            var normalized = NoteImageHelper.NormalizeHexOrNull(cellBg);
            if (normalized is not null && !string.Equals(Page.TableCellBgColor, normalized, StringComparison.OrdinalIgnoreCase))
            {
                Page.TableCellBgColor = normalized;
                changed = true;
            }
        }

        if (!changed)
        {
            return false;
        }

        TouchUpdatedAt();
        RaisePropertyChanged(nameof(TableHeaderBgColor), nameof(TableFirstColumnBgColor), nameof(TableCellBgColor));
        return true;
    }

    public void TouchUpdatedAt()
    {
        Page.UpdatedAt = DateTimeOffset.Now;
        _parentSection.TouchUpdatedAt();
        RaisePropertyChanged(nameof(UpdatedAt), nameof(DateLineText), nameof(ShortDateText));
    }
}

/// <summary>SinNote 页面 ViewModel：管理分区、页与离线 Typora 风格 Markdown 编辑器状态，并与 WebDAV 共用 AppData。</summary>
public sealed class NotesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _settingsSaveTimer;

    private NoteSectionItemViewModel? _selectedSection;
    private NotePageItemViewModel? _selectedPage;
    private string _statusMessage = "Typora 即时渲染模式：支持表格、代码块、Ctrl+V 粘贴截图、Ctrl+Z 撤销、Ctrl+F/E 查找";
    private bool _hasPendingChanges;
    private bool _hasPendingSettingsSave;

    private string _searchText = string.Empty;
    private NoteSearchScope _searchScope = NoteSearchScope.CurrentPage;
    private bool _isSearchOpen;
    private bool _isSearchDropdownOpen;
    private int _currentMatchIndex = -1;
    private bool _suppressSearchRefreshOnPageChange;

    public NotesViewModel(AppServices services)
    {
        _services = services;

        _autoSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            SaveNow();
        };

        _settingsSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            if (_hasPendingSettingsSave)
            {
                _hasPendingSettingsSave = false;
                _services.SaveSettings();
            }
        };

        AddSectionCommand = new RelayCommand(AddSection);
        RenameSectionCommand = new RelayCommand(RenameSection, p => (p as NoteSectionItemViewModel ?? SelectedSection) is not null);
        ChangeSectionColorCommand = new RelayCommand(ChangeSectionColor, _ => SelectedSection is not null);
        DeleteSectionCommand = new RelayCommand(DeleteSection, p => (p as NoteSectionItemViewModel ?? SelectedSection) is not null);

        AddPageCommand = new RelayCommand(AddPage);
        AddTablePageCommand = new RelayCommand(AddTablePage);
        RenamePageCommand = new RelayCommand(RenamePage, p => (p as NotePageItemViewModel ?? SelectedPage) is not null);
        DeletePageCommand = new RelayCommand(DeletePage, p => (p as NotePageItemViewModel ?? SelectedPage) is not null);

        AddTableBlockCommand = new RelayCommand(InsertDefaultTable);
        PasteImageCommand = new RelayCommand(PasteImageFromClipboard);
        InsertImageFileCommand = new RelayCommand(InsertImageFromFile);
        ChangeTableHeaderColorCommand = new RelayCommand(ChangeTableHeaderColor, _ => SelectedPage is not null);
        ChangeTableFirstColumnColorCommand = new RelayCommand(ChangeTableFirstColumnColor, _ => SelectedPage is not null);
        ChangeTableCellColorCommand = new RelayCommand(ChangeTableCellColor, _ => SelectedPage is not null);
        ResetTableColorsCommand = new RelayCommand(() => ResetTableColors(), () => SelectedPage is not null);

        ChangeFontFamilyCommand = new RelayCommand(ChangeFontFamily);
        ResetZoomCommand = new RelayCommand(ResetZoom);
        ResetTypographyCommand = new RelayCommand(ResetTypography);

        CloseSearchCommand = new RelayCommand(CloseSearch);
        ClearSearchCommand = new RelayCommand(ClearSearch);
        ToggleSearchScopeCommand = new RelayCommand(ToggleSearchScope);
        NextSearchMatchCommand = new RelayCommand(() => StepSearchMatch(1), () => SearchResults.Count > 0);
        PrevSearchMatchCommand = new RelayCommand(() => StepSearchMatch(-1), () => SearchResults.Count > 0);
        SelectSearchResultCommand = new RelayCommand(p =>
        {
            if (p is NoteSearchResultItem item)
            {
                SelectSearchResult(item, closeDropdown: true, focusTarget: true);
            }
        });

        OpenTrashCommand = new RelayCommand(() => OpenTrashRequested?.Invoke());
        RestoreTrashItemCommand = new RelayCommand(RestoreTrashItem, p => p is NoteTrashItemViewModel);
        DeleteTrashItemPermanentlyCommand = new RelayCommand(DeleteTrashItemPermanently, p => p is NoteTrashItemViewModel);
        EmptyTrashCommand = new RelayCommand(EmptyTrash, () => TrashItems.Count > 0);
    }

    // ---------- 预设调色板 ----------

    public static IReadOnlyList<NoteColorPreset> SectionColorPresets { get; } = new List<NoteColorPreset>
    {
        new("海蓝", "#2F75B5"),
        new("紫罗兰", "#A371F7"),
        new("翠绿", "#3FB950"),
        new("琥珀", "#D29922"),
        new("珊瑚红", "#F85149"),
        new("青碧", "#2EA8A8"),
        new("樱粉", "#DB61A2"),
        new("石板灰", "#6E7681"),
    };

    public static IReadOnlyList<NoteColorPreset> TableHeaderColorPresets { get; } = new List<NoteColorPreset>
    {
        new("OneNote 蓝", "#2F75B5"),
        new("深海蓝", "#1F4E78"),
        new("暮紫", "#5B3E96"),
        new("青绿", "#1F7A7A"),
        new("森林绿", "#2E7D32"),
        new("琥珀橙", "#B26A00"),
        new("砖红", "#A52A2A"),
        new("石板灰", "#334155"),
    };

    public static IReadOnlyList<NoteColorPreset> TableCellColorPresets { get; } = new List<NoteColorPreset>
    {
        new("深蓝灰", "#1E2D3D"),
        new("OneNote 浅蓝", "#BDD7EE"),
        new("深炭灰", "#1C2128"),
        new("深紫灰", "#261E38"),
        new("深绿灰", "#1D3126"),
        new("深棕灰", "#2D2618"),
        new("浅灰白", "#E2E8F0"),
        new("浅米黄", "#FFF2CC"),
    };

    public static IReadOnlyList<NoteFontPreset> EditorFontFamilyPresets { get; } = new List<NoteFontPreset>
    {
        new("微软雅黑（默认）", AppSettings.DefaultNoteFontFamily),
        new("宋体", "\"SimSun\", \"NSimSun\", serif"),
        new("黑体", "\"SimHei\", \"Microsoft YaHei UI\", sans-serif"),
        new("楷体", "\"KaiTi\", \"STKaiti\", serif"),
        new("仿宋", "\"FangSong\", \"STFangsong\", serif"),
        new("等线", "\"DengXian\", \"Microsoft YaHei UI\", sans-serif"),
        new("等宽代码", "\"Cascadia Mono\", \"Consolas\", \"Microsoft YaHei UI\", monospace"),
    };

    // ---------- 界面回调与编辑器事件 ----------

    /// <summary>(title, label, initialValue) -> newText 或 null（取消）。</summary>
    public Func<string, string, string, string?>? PromptText { get; set; }

    public Func<string, string, bool>? Confirm { get; set; }

    public Action<string, string>? ShowMessage { get; set; }

    /// <summary>(image, caption, base64)。</summary>
    public Action<BitmapSource, string?, string?>? PreviewImage { get; set; }

    /// <summary>请求主窗口弹出 SinNote 回收站窗口。</summary>
    public Action? OpenTrashRequested { get; set; }

    /// <summary>通知视图滚动定位并选中命中文本；bool 表示是否转移键盘焦点。</summary>
    public event Action<NoteSearchResultItem, bool>? NavigateToMatchRequested;

    /// <summary>请求编辑器在当前光标处插入一段 Markdown。</summary>
    public event Action<string>? InsertMarkdownRequested;

    /// <summary>通知编辑器加载或重载指定页（bool=true 表示强制重置撤销栈与正文）。</summary>
    public event Action<NotePageItemViewModel?, bool>? EditorReloadPageRequested;

    /// <summary>通知编辑器刷新当前页表格 CSS 配色变量。</summary>
    public event Action<NotePageItemViewModel>? TableColorsChangedRequested;

    /// <summary>通知编辑器清空当前页内查找高亮。</summary>
    public event Action? ClearSearchHighlightRequested;

    /// <summary>在强制刷盘前通知宿主同步刷回未防抖完的编辑内容。</summary>
    public event Action? FlushEditorRequested;

    /// <summary>通知编辑器应用最新的字体格式与画布缩放比例。</summary>
    public event Action<string, int>? TypographyChangedRequested;

    // ---------- 状态与集合 ----------

    public ObservableCollection<NoteSectionItemViewModel> Sections { get; } = new();

    public ObservableCollection<NoteTrashItemViewModel> TrashItems { get; } = new();

    public bool HasTrashItems => TrashItems.Count > 0;

    public bool HasNoTrashItems => TrashItems.Count == 0;

    public string TrashButtonText => TrashItems.Count > 0 ? $"回收站 ({TrashItems.Count})" : "回收站";

    public string TrashSummaryText => TrashItems.Count == 0
        ? "回收站为空"
        : $"共 {TrashItems.Count} 项（最多保留 {NoteTrashItem.DefaultRetentionDays} 天）";

    public NoteSectionItemViewModel? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (_selectedSection == value)
            {
                return;
            }

            FlushPendingSave();
            if (_selectedSection is not null)
            {
                _selectedSection.IsSelected = false;
            }

            _selectedSection = value;
            if (_selectedSection is not null)
            {
                _selectedSection.IsSelected = true;
            }

            OnPropertyChanged();
            RaisePropertyChanged(nameof(HasSelectedSection), nameof(HasNoSections));

            SelectedPage = _selectedSection?.Pages.FirstOrDefault();
            RaiseSectionAndPageCommandsCanExecute();
        }
    }

    public NotePageItemViewModel? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (_selectedPage == value)
            {
                return;
            }

            FlushPendingSave();
            if (_selectedPage is not null)
            {
                _selectedPage.IsSelected = false;
            }

            _selectedPage = value;
            if (_selectedPage is not null)
            {
                _selectedPage.IsSelected = true;
            }

            OnPropertyChanged();
            RaisePropertyChanged(
                nameof(HasSelectedPage),
                nameof(HasNoSelectedPage),
                nameof(CurrentTableHeaderBgColor),
                nameof(CurrentTableFirstColumnBgColor),
                nameof(CurrentTableCellBgColor));
            RaiseSectionAndPageCommandsCanExecute();
            EditorReloadPageRequested?.Invoke(_selectedPage, false);

            if (!_suppressSearchRefreshOnPageChange && IsSearchOpen && SearchScope == NoteSearchScope.CurrentPage)
            {
                RunSearch(navigateFirst: false);
            }
        }
    }

    public bool HasSelectedSection => _selectedSection is not null;

    public bool HasNoSections => Sections.Count == 0;

    public bool HasSelectedPage => _selectedPage is not null;

    public bool HasNoSelectedPage => _selectedPage is null;

    public string CurrentTableHeaderBgColor => _selectedPage?.TableHeaderBgColor ?? NoteTable.DefaultHeaderBgColor;

    public string CurrentTableFirstColumnBgColor => _selectedPage?.TableFirstColumnBgColor ?? NoteTable.DefaultFirstColumnBgColor;

    public string CurrentTableCellBgColor => _selectedPage?.TableCellBgColor ?? NoteTable.DefaultCellBgColor;

    public string NoteFontFamily => string.IsNullOrWhiteSpace(_services.Settings.NoteFontFamily)
        ? AppSettings.DefaultNoteFontFamily
        : _services.Settings.NoteFontFamily;

    public int NoteZoomPercent => _services.Settings.NoteZoomPercent is < 50 or > 200
        ? AppSettings.DefaultNoteZoomPercent
        : _services.Settings.NoteZoomPercent;

    public string FontButtonLabel => NoteZoomPercent == 100 ? "字体" : $"字体 {NoteZoomPercent}%";

    public string ZoomSummaryText => $"当前缩放：{NoteZoomPercent}%（正文区按 Ctrl+滚轮 缩放，Ctrl+0 还原）";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // ---------- 查找（Ctrl+F 本页 / Ctrl+E 全部分区） ----------

    public ObservableCollection<NoteSearchResultItem> SearchResults { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                RunSearch(navigateFirst: false);
                if (!string.IsNullOrWhiteSpace(_searchText) && IsSearchOpen)
                {
                    IsSearchDropdownOpen = true;
                }
            }
        }
    }

    public NoteSearchScope SearchScope
    {
        get => _searchScope;
        set
        {
            if (SetProperty(ref _searchScope, value))
            {
                RaisePropertyChanged(
                    nameof(IsCurrentPageScope),
                    nameof(IsAllSectionsScope),
                    nameof(SearchScopeHintText),
                    nameof(SwitchScopeLinkText),
                    nameof(SearchHeaderText));
                RunSearch(navigateFirst: false);
            }
        }
    }

    public bool IsCurrentPageScope => _searchScope == NoteSearchScope.CurrentPage;

    public bool IsAllSectionsScope => _searchScope == NoteSearchScope.AllSections;

    public string SearchScopeHintText => _searchScope == NoteSearchScope.CurrentPage
        ? "当前页面内查找 (Ctrl+F)"
        : "全部分区查找 (Ctrl+E)";

    public string SwitchScopeLinkText => _searchScope == NoteSearchScope.CurrentPage
        ? "查找全部分区 (Ctrl+E)"
        : "在页面上查找 (Ctrl+F)";

    public string SearchHeaderText
    {
        get
        {
            var q = _searchText.Trim();
            if (_searchScope == NoteSearchScope.CurrentPage)
            {
                return q.Length == 0
                    ? "当前页面内查找（输入关键词）"
                    : $"本页结果：{q}（{SearchResults.Count} 处）";
            }

            return q.Length == 0
                ? "全部分区查找（输入关键词）"
                : $"已完成：全部分区（{SearchResults.Count} 页匹配）";
        }
    }

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set => SetProperty(ref _isSearchOpen, value);
    }

    public bool IsSearchDropdownOpen
    {
        get => _isSearchDropdownOpen;
        set => SetProperty(ref _isSearchDropdownOpen, value);
    }

    public bool HasSearchResults => SearchResults.Count > 0;

    public string MatchProgressText => SearchResults.Count == 0
        ? (string.IsNullOrWhiteSpace(_searchText) ? string.Empty : "0 处")
        : (_currentMatchIndex < 0 ? $"{SearchResults.Count} 项" : $"{_currentMatchIndex + 1}/{SearchResults.Count}");

    // ---------- 命令 ----------

    public ICommand AddSectionCommand { get; }
    public ICommand RenameSectionCommand { get; }
    public ICommand ChangeSectionColorCommand { get; }
    public ICommand DeleteSectionCommand { get; }

    public ICommand AddPageCommand { get; }
    public ICommand AddTablePageCommand { get; }
    public ICommand RenamePageCommand { get; }
    public ICommand DeletePageCommand { get; }

    public ICommand AddTableBlockCommand { get; }
    public ICommand PasteImageCommand { get; }
    public ICommand InsertImageFileCommand { get; }
    public ICommand ChangeTableHeaderColorCommand { get; }
    public ICommand ChangeTableFirstColumnColorCommand { get; }
    public ICommand ChangeTableCellColorCommand { get; }
    public ICommand ResetTableColorsCommand { get; }

    public ICommand ChangeFontFamilyCommand { get; }
    public ICommand ResetZoomCommand { get; }
    public ICommand ResetTypographyCommand { get; }

    public ICommand CloseSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ToggleSearchScopeCommand { get; }
    public ICommand NextSearchMatchCommand { get; }
    public ICommand PrevSearchMatchCommand { get; }
    public ICommand SelectSearchResultCommand { get; }

    public ICommand OpenTrashCommand { get; }
    public ICommand RestoreTrashItemCommand { get; }
    public ICommand DeleteTrashItemPermanentlyCommand { get; }
    public ICommand EmptyTrashCommand { get; }

    // ---------- 编辑器桥接回调 ----------

    /// <summary>接收来自 WebView2 Vditor 编辑器的内容改动（按 pageId 精确落位，防止切页竞态串页）。</summary>
    public void UpdatePageMarkdownFromEditor(string pageId, string markdown)
    {
        var target = FindPageById(pageId);
        if (target is null)
        {
            return;
        }

        if (target.SetMarkdownFromEditor(markdown))
        {
            ScheduleSave();
        }
    }

    /// <summary>将编辑器粘贴/上传的图片注册到目标页 Images 字典并排队落盘。</summary>
    public void RegisterPastedImage(string pageId, string imageId, string mimeType, string base64)
    {
        if (string.IsNullOrWhiteSpace(imageId) || string.IsNullOrWhiteSpace(base64))
        {
            return;
        }

        var target = FindPageById(pageId) ?? SelectedPage;
        if (target is null)
        {
            return;
        }

        target.Page.Images[imageId] = new NoteImageAsset
        {
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType.Trim(),
            Base64 = base64.Trim(),
        };
        target.TouchUpdatedAt();
        ScheduleSave();
        StatusMessage = "已插入图片（单击选中后按退格可删除，双击放大预览）";
    }

    /// <summary>接收来自 Vditor 工具栏内聚调色板的配色变更并落盘。</summary>
    public void UpdateTableColorsFromEditor(string? headerBg, string? firstColBg, string? cellBg)
    {
        if (SelectedPage is null)
        {
            return;
        }

        if (SelectedPage.ApplyTableColors(headerBg, firstColBg, cellBg))
        {
            RaisePropertyChanged(
                nameof(CurrentTableHeaderBgColor),
                nameof(CurrentTableFirstColumnBgColor),
                nameof(CurrentTableCellBgColor));
            SaveNow();
            StatusMessage = $"已更新表格配色（首行 {SelectedPage.TableHeaderBgColor} · 首列 {SelectedPage.TableFirstColumnBgColor} · 内容 {SelectedPage.TableCellBgColor}）";
        }
    }

    /// <summary>接收来自 Vditor 工具栏内聚字体面板的字体格式变更并落盘。</summary>
    public void UpdateFontFamilyFromEditor(string? fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily))
        {
            return;
        }

        var next = fontFamily.Trim();
        if (string.Equals(_services.Settings.NoteFontFamily, next, StringComparison.Ordinal))
        {
            return;
        }

        _services.Settings.NoteFontFamily = next;
        _services.SaveSettings();
        OnPropertyChanged(nameof(NoteFontFamily));

        var name = EditorFontFamilyPresets
            .FirstOrDefault(p => string.Equals(p.CssValue, NoteFontFamily, StringComparison.OrdinalIgnoreCase))?.Name
            ?? NoteFontFamily.Split(',')[0].Trim().Trim('"', '\'');
        StatusMessage = $"已切换正文默认字体：{name}";
    }

    /// <summary>双击 Vditor 内嵌图片时打开高清大图预览弹窗。</summary>
    public void PreviewDataUriImage(string dataUri, string? caption)
    {
        if (string.IsNullOrWhiteSpace(dataUri))
        {
            return;
        }

        var marker = ";base64,";
        var idx = dataUri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var base64 = idx >= 0 ? dataUri[(idx + marker.Length)..].Trim() : dataUri.Trim();
        var bitmap = NoteImageHelper.DecodeBase64(base64);
        if (bitmap is not null)
        {
            PreviewImage?.Invoke(bitmap, caption, base64);
        }
    }

    /// <summary>同步编辑器 Ctrl+滚轮缩放后的百分比并防抖落盘。</summary>
    public void UpdateZoomFromEditor(int zoomPercent)
    {
        var clamped = Math.Clamp(zoomPercent, 50, 200);
        if (_services.Settings.NoteZoomPercent == clamped)
        {
            return;
        }

        _services.Settings.NoteZoomPercent = clamped;
        _hasPendingSettingsSave = true;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
        RaisePropertyChanged(nameof(NoteZoomPercent), nameof(FontButtonLabel), nameof(ZoomSummaryText));
        StatusMessage = $"画布缩放：{clamped}%（正文区 Ctrl+滚轮 缩放，Ctrl+0 还原 100%）";
    }

    // ---------- 字体格式与缩放 ----------

    private void ChangeFontFamily(object? parameter)
    {
        if (parameter is not string cssValue || string.IsNullOrWhiteSpace(cssValue))
        {
            return;
        }

        _services.Settings.NoteFontFamily = cssValue.Trim();
        _services.SaveSettings();
        OnPropertyChanged(nameof(NoteFontFamily));
        TypographyChangedRequested?.Invoke(NoteFontFamily, NoteZoomPercent);

        var name = EditorFontFamilyPresets
            .FirstOrDefault(p => string.Equals(p.CssValue, NoteFontFamily, StringComparison.OrdinalIgnoreCase))?.Name
            ?? NoteFontFamily.Split(',')[0].Trim().Trim('"', '\'');
        StatusMessage = $"已切换正文默认字体：{name}";
    }

    private void ResetZoom()
    {
        _services.Settings.NoteZoomPercent = AppSettings.DefaultNoteZoomPercent;
        _services.SaveSettings();
        RaisePropertyChanged(nameof(NoteZoomPercent), nameof(FontButtonLabel), nameof(ZoomSummaryText));
        TypographyChangedRequested?.Invoke(NoteFontFamily, NoteZoomPercent);
        StatusMessage = "画布缩放已还原为 100%";
    }

    private void ResetTypography()
    {
        _services.Settings.NoteFontFamily = AppSettings.DefaultNoteFontFamily;
        _services.Settings.NoteZoomPercent = AppSettings.DefaultNoteZoomPercent;
        _services.SaveSettings();
        RaisePropertyChanged(nameof(NoteFontFamily), nameof(NoteZoomPercent), nameof(FontButtonLabel), nameof(ZoomSummaryText));
        TypographyChangedRequested?.Invoke(NoteFontFamily, NoteZoomPercent);
        StatusMessage = "已恢复默认字体格式与 100% 缩放";
    }

    // ---------- 查找操作 ----------

    public void OpenSearch(NoteSearchScope scope)
    {
        if (scope == NoteSearchScope.AllSections && SelectedPage is null)
        {
            var firstWithPage = Sections.FirstOrDefault(s => s.Pages.Count > 0);
            if (firstWithPage is not null)
            {
                SelectedSection = firstWithPage;
                SelectedPage = firstWithPage.Pages.FirstOrDefault();
            }
        }

        SearchScope = scope;
        IsSearchOpen = true;
        RunSearch(navigateFirst: false);
        IsSearchDropdownOpen = scope == NoteSearchScope.AllSections || !string.IsNullOrWhiteSpace(_searchText);
    }

    private void CloseSearch()
    {
        IsSearchDropdownOpen = false;
        IsSearchOpen = false;
        ClearSearchHighlightRequested?.Invoke();
    }

    private void ClearSearch()
    {
        SearchText = string.Empty;
        IsSearchDropdownOpen = false;
        ClearSearchHighlightRequested?.Invoke();
    }

    private void ToggleSearchScope()
    {
        SearchScope = _searchScope == NoteSearchScope.CurrentPage
            ? NoteSearchScope.AllSections
            : NoteSearchScope.CurrentPage;
        IsSearchOpen = true;
        IsSearchDropdownOpen = true;
    }

    private void StepSearchMatch(int delta)
    {
        if (SearchResults.Count == 0)
        {
            RunSearch(navigateFirst: false);
            if (SearchResults.Count == 0)
            {
                return;
            }
        }

        var nextIdx = _currentMatchIndex < 0
            ? (delta >= 0 ? 0 : SearchResults.Count - 1)
            : (_currentMatchIndex + delta + SearchResults.Count) % SearchResults.Count;

        _currentMatchIndex = nextIdx;
        RaisePropertyChanged(nameof(MatchProgressText));
        SelectSearchResult(SearchResults[nextIdx], closeDropdown: false, focusTarget: false);
    }

    private void SelectSearchResult(NoteSearchResultItem item, bool closeDropdown, bool focusTarget)
    {
        var idx = SearchResults.IndexOf(item);
        if (idx >= 0)
        {
            _currentMatchIndex = idx;
            RaisePropertyChanged(nameof(MatchProgressText));
        }

        _suppressSearchRefreshOnPageChange = true;
        try
        {
            if (!ReferenceEquals(SelectedSection, item.Section))
            {
                SelectedSection = item.Section;
            }

            if (!ReferenceEquals(SelectedPage, item.Page))
            {
                SelectedPage = item.Page;
            }
        }
        finally
        {
            _suppressSearchRefreshOnPageChange = false;
        }

        if (closeDropdown)
        {
            IsSearchDropdownOpen = false;
        }

        NavigateToMatchRequested?.Invoke(item, focusTarget);
    }

    private void RunSearch(bool navigateFirst)
    {
        SearchResults.Clear();
        _currentMatchIndex = -1;

        var query = _searchText.Trim();
        if (query.Length > 0)
        {
            const int maxResults = 200;
            if (_searchScope == NoteSearchScope.CurrentPage)
            {
                if (SelectedSection is not null && SelectedPage is not null)
                {
                    CollectPageMatches(SelectedSection, SelectedPage, query, groupByPage: false, SearchResults, maxResults);
                }
            }
            else
            {
                foreach (var section in Sections)
                {
                    foreach (var page in section.Pages)
                    {
                        CollectPageMatches(section, page, query, groupByPage: true, SearchResults, maxResults);
                        if (SearchResults.Count >= maxResults)
                        {
                            break;
                        }
                    }

                    if (SearchResults.Count >= maxResults)
                    {
                        break;
                    }
                }
            }
        }
        else
        {
            ClearSearchHighlightRequested?.Invoke();
        }

        RaisePropertyChanged(nameof(HasSearchResults), nameof(SearchHeaderText), nameof(MatchProgressText));
        (NextSearchMatchCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PrevSearchMatchCommand as RelayCommand)?.RaiseCanExecuteChanged();

        if (navigateFirst && SearchResults.Count > 0)
        {
            _currentMatchIndex = 0;
            RaisePropertyChanged(nameof(MatchProgressText));
            SelectSearchResult(SearchResults[0], closeDropdown: false, focusTarget: false);
        }
    }

    private static void CollectPageMatches(
        NoteSectionItemViewModel section,
        NotePageItemViewModel page,
        string query,
        bool groupByPage,
        ICollection<NoteSearchResultItem> output,
        int maxResults)
    {
        var titleIndices = FindAllIndices(page.Title, query, maxResults);
        var searchableBody = NotePage.ToSearchablePlainText(page.Page.EnsureMarkdown());
        var bodyIndices = FindAllIndices(searchableBody, query, maxResults);

        var totalHits = titleIndices.Count + bodyIndices.Count;
        if (totalHits == 0)
        {
            return;
        }

        if (groupByPage)
        {
            if (titleIndices.Count > 0)
            {
                var foundAt = titleIndices[0];
                var tag = bodyIndices.Count > 0
                    ? $"标题+正文 · {totalHits} 处"
                    : (titleIndices.Count > 1 ? $"标题 · {titleIndices.Count} 处" : "标题");
                output.Add(new NoteSearchResultItem
                {
                    Section = section,
                    Page = page,
                    IsTitleMatch = true,
                    MatchIndex = foundAt,
                    MatchLength = query.Length,
                    OccurrenceIndex = 0,
                    SourceTag = tag,
                    Snippet = BuildSnippet(page.Title, foundAt, query.Length),
                });
            }
            else
            {
                var foundAt = bodyIndices[0];
                var tag = bodyIndices.Count > 1 ? $"正文 · {bodyIndices.Count} 处" : "正文";
                output.Add(new NoteSearchResultItem
                {
                    Section = section,
                    Page = page,
                    IsTitleMatch = false,
                    MatchIndex = foundAt,
                    MatchLength = query.Length,
                    OccurrenceIndex = 0,
                    SourceTag = tag,
                    Snippet = BuildSnippet(searchableBody, foundAt, query.Length),
                });
            }

            return;
        }

        foreach (var foundAt in titleIndices)
        {
            if (output.Count >= maxResults)
            {
                return;
            }

            output.Add(new NoteSearchResultItem
            {
                Section = section,
                Page = page,
                IsTitleMatch = true,
                MatchIndex = foundAt,
                MatchLength = query.Length,
                OccurrenceIndex = 0,
                SourceTag = "标题",
                Snippet = BuildSnippet(page.Title, foundAt, query.Length),
            });
        }

        for (var i = 0; i < bodyIndices.Count && output.Count < maxResults; i++)
        {
            var foundAt = bodyIndices[i];
            output.Add(new NoteSearchResultItem
            {
                Section = section,
                Page = page,
                IsTitleMatch = false,
                MatchIndex = foundAt,
                MatchLength = query.Length,
                OccurrenceIndex = i,
                SourceTag = bodyIndices.Count > 1 ? $"正文 第{i + 1}处" : "正文",
                Snippet = BuildSnippet(searchableBody, foundAt, query.Length),
            });
        }
    }

    private static List<int> FindAllIndices(string? content, string query, int limit)
    {
        var result = new List<int>();
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(query))
        {
            return result;
        }

        var startIndex = 0;
        while (result.Count < limit)
        {
            var foundAt = content.IndexOf(query, startIndex, StringComparison.OrdinalIgnoreCase);
            if (foundAt < 0)
            {
                break;
            }

            result.Add(foundAt);
            startIndex = foundAt + Math.Max(1, query.Length);
        }

        return result;
    }

    private static string BuildSnippet(string content, int matchIndex, int matchLength)
    {
        var singleLine = content.Replace('\r', ' ').Replace('\n', ' ');
        var left = Math.Max(0, matchIndex - 14);
        var right = Math.Min(singleLine.Length, matchIndex + matchLength + 26);
        var slice = singleLine[left..right].Trim();
        if (left > 0)
        {
            slice = "…" + slice;
        }

        if (right < singleLine.Length)
        {
            slice += "…";
        }

        return slice;
    }

    // ---------- 生命周期与持久化 ----------

    /// <summary>从 _services.Data.NoteSections 重新构建界面列表（用于初始化与备份恢复后）。</summary>
    public void ReloadFromStore()
    {
        _autoSaveTimer.Stop();
        _hasPendingChanges = false;

        var rawSections = NoteSection.NormalizeList(_services.Data.NoteSections);
        _services.Data.NoteSections = rawSections;

        var rawTrash = NoteTrashItem.NormalizeList(_services.Data.NoteTrash);
        var prunedExpiredTrash = NoteTrashItem.PruneExpired(rawTrash);
        _services.Data.NoteTrash = rawTrash;

        var healedLegacyInlineImages = prunedExpiredTrash > 0;
        foreach (var sec in rawSections)
        {
            foreach (var p in sec.Pages)
            {
                var beforeLen = p.Markdown?.Length ?? -1;
                var beforeImagesCount = p.Images?.Count ?? 0;
                p.EnsureMarkdown();
                if (p.PruneUnreferencedImages())
                {
                    healedLegacyInlineImages = true;
                }

                VditorAssetHost.SyncPageImagesToCache(p);
                if ((p.Markdown?.Length ?? -1) != beforeLen || (p.Images?.Count ?? 0) != beforeImagesCount)
                {
                    healedLegacyInlineImages = true;
                }
            }
        }

        VditorAssetHost.PruneUnreferencedCachedImages(rawSections, rawTrash);

        Sections.Clear();
        foreach (var sec in rawSections)
        {
            Sections.Add(new NoteSectionItemViewModel(this, sec));
        }

        TrashItems.Clear();
        foreach (var item in rawTrash.OrderByDescending(i => i.DeletedAt))
        {
            TrashItems.Add(new NoteTrashItemViewModel(item));
        }

        RefreshTrashUI();

        _selectedSection = null;
        _selectedPage = null;
        OnPropertyChanged(nameof(SelectedSection));
        OnPropertyChanged(nameof(SelectedPage));
        RaisePropertyChanged(
            nameof(HasSelectedSection),
            nameof(HasNoSections),
            nameof(HasSelectedPage),
            nameof(HasNoSelectedPage),
            nameof(CurrentTableHeaderBgColor),
            nameof(CurrentTableFirstColumnBgColor),
            nameof(CurrentTableCellBgColor));

        SelectedSection = Sections.FirstOrDefault();
        EditorReloadPageRequested?.Invoke(_selectedPage, true);
        RaiseSectionAndPageCommandsCanExecute();

        if (healedLegacyInlineImages)
        {
            ScheduleSave();
        }
    }

    public void ScheduleSave()
    {
        _hasPendingChanges = true;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    /// <summary>备份或导出前调用：刷回未落盘改动并清理正文不再引用的孤儿图片，瘦身备份体积。</summary>
    public void PrepareForBackup()
    {
        FlushPendingSave();

        var pruned = false;
        foreach (var sec in _services.Data.NoteSections)
        {
            foreach (var page in sec.Pages)
            {
                if (page.PruneUnreferencedImages())
                {
                    pruned = true;
                }
            }
        }

        if (pruned)
        {
            SaveNow();
        }

        VditorAssetHost.PruneUnreferencedCachedImages(_services.Data.NoteSections, _services.Data.NoteTrash);
    }

    public void FlushPendingSave()
    {
        FlushEditorRequested?.Invoke();
        _autoSaveTimer.Stop();
        if (_hasPendingChanges)
        {
            SaveNow();
        }

        _settingsSaveTimer.Stop();
        if (_hasPendingSettingsSave)
        {
            _hasPendingSettingsSave = false;
            _services.SaveSettings();
        }
    }

    public void SaveNow()
    {
        _autoSaveTimer.Stop();
        try
        {
            _services.SaveData();
            _hasPendingChanges = false;
            StatusMessage = $"已自动保存（{DateTime.Now:HH:mm:ss}）";
        }
        catch (Exception ex)
        {
            AppLog.Write("保存笔记数据失败", ex);
            StatusMessage = "保存失败：" + ex.Message;
        }
    }

    public void ReportHint(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusMessage = message;
        }
    }

    // ---------- 分区操作 ----------

    private void AddSection()
    {
        var name = PromptText is not null
            ? PromptText.Invoke("新建分区", "分区名称", $"新分区 {Sections.Count + 1}")
            : $"新分区 {Sections.Count + 1}";
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var color = SectionColorPresets[Sections.Count % SectionColorPresets.Count].Hex;
        var initialPage = CreateBlankPage("无标题页", withDefaultTable: false);
        var section = new NoteSection
        {
            Name = name.Trim(),
            ColorHex = color,
            Pages = new List<NotePage> { initialPage },
        };

        _services.Data.NoteSections.Add(section);
        var sectionVm = new NoteSectionItemViewModel(this, section);
        Sections.Add(sectionVm);

        RaisePropertyChanged(nameof(HasNoSections));
        SelectedSection = sectionVm;
        SelectedPage = sectionVm.Pages.FirstOrDefault();
        SaveNow();
        StatusMessage = $"已新建分区「{section.Name}」";
    }

    private void RenameSection(object? parameter)
    {
        var target = parameter as NoteSectionItemViewModel ?? SelectedSection;
        if (target is null)
        {
            return;
        }

        var nextName = PromptText?.Invoke("重命名分区", "分区名称", target.Name);
        if (string.IsNullOrWhiteSpace(nextName))
        {
            return;
        }

        target.Name = nextName;
        SaveNow();
        StatusMessage = $"分区已重命名为「{target.Name}」";
    }

    private void ChangeSectionColor(object? parameter)
    {
        if (SelectedSection is null)
        {
            return;
        }

        if (parameter is string hex && NoteImageHelper.NormalizeHexOrNull(hex) is not null)
        {
            SelectedSection.ColorHex = hex;
            SaveNow();
            StatusMessage = $"已更新分区「{SelectedSection.Name}」颜色";
        }
    }

    private void DeleteSection(object? parameter)
    {
        var target = parameter as NoteSectionItemViewModel ?? SelectedSection;
        if (target is null)
        {
            return;
        }

        var ok = Confirm?.Invoke(
            "移入回收站",
            $"确定将分区「{target.Name}」（含 {target.Pages.Count} 个页）移入回收站吗？\n删除的内容将保留 {NoteTrashItem.DefaultRetentionDays} 天，可在回收站中随时还原。") ?? false;
        if (!ok)
        {
            return;
        }

        FlushEditorRequested?.Invoke();

        var trashItem = new NoteTrashItem
        {
            IsSection = true,
            DeletedAt = DateTimeOffset.Now,
            OriginalSectionId = target.Id,
            OriginalSectionName = target.Name,
            OriginalSectionColorHex = target.ColorHex,
            Section = target.Section.Clone(),
        };
        _services.Data.NoteTrash.Insert(0, trashItem);
        TrashItems.Insert(0, new NoteTrashItemViewModel(trashItem));
        RefreshTrashUI();

        var idx = Sections.IndexOf(target);
        _services.Data.NoteSections.Remove(target.Section);
        Sections.Remove(target);

        RaisePropertyChanged(nameof(HasNoSections));
        SelectedSection = Sections.Count == 0
            ? null
            : Sections[Math.Clamp(idx, 0, Sections.Count - 1)];

        SaveNow();
        StatusMessage = $"分区「{target.Name}」已移入回收站（保留 {NoteTrashItem.DefaultRetentionDays} 天）";
    }

    // ---------- 页操作 ----------

    private void AddPage() => AddPageCore(withDefaultTable: false, defaultTitle: "无标题页");

    private void AddTablePage() => AddPageCore(withDefaultTable: true, defaultTitle: "新建表格页");

    private void AddPageCore(bool withDefaultTable, string defaultTitle)
    {
        var section = EnsureActiveSection();
        var page = CreateBlankPage(defaultTitle, withDefaultTable);
        section.Section.Pages.Add(page);

        var pageVm = new NotePageItemViewModel(this, section, page);
        section.Pages.Add(pageVm);
        section.TouchUpdatedAt();

        SelectedPage = pageVm;
        SaveNow();
        StatusMessage = withDefaultTable ? "已新建带 3×3 表格的页" : "已添加新页";
    }

    private void RenamePage(object? parameter)
    {
        var target = parameter as NotePageItemViewModel ?? SelectedPage;
        if (target is null)
        {
            return;
        }

        var nextTitle = PromptText?.Invoke("重命名页", "页标题", target.Title);
        if (string.IsNullOrWhiteSpace(nextTitle))
        {
            return;
        }

        target.Title = nextTitle.Trim();
        SaveNow();
        StatusMessage = $"页已重命名为「{target.Title}」";
    }

    private void DeletePage(object? parameter)
    {
        var target = parameter as NotePageItemViewModel ?? SelectedPage;
        var section = SelectedSection;
        if (target is null || section is null)
        {
            return;
        }

        var displayTitle = string.IsNullOrWhiteSpace(target.Title) ? "无标题页" : target.Title;
        var ok = Confirm?.Invoke(
            "移入回收站",
            $"确定将页「{displayTitle}」移入回收站吗？\n删除的内容将保留 {NoteTrashItem.DefaultRetentionDays} 天，可在回收站中随时还原。") ?? false;
        if (!ok)
        {
            return;
        }

        FlushEditorRequested?.Invoke();

        var trashItem = new NoteTrashItem
        {
            IsSection = false,
            DeletedAt = DateTimeOffset.Now,
            OriginalSectionId = section.Id,
            OriginalSectionName = section.Name,
            OriginalSectionColorHex = section.ColorHex,
            Page = target.Page.Clone(),
        };
        _services.Data.NoteTrash.Insert(0, trashItem);
        TrashItems.Insert(0, new NoteTrashItemViewModel(trashItem));
        RefreshTrashUI();

        var idx = section.Pages.IndexOf(target);
        section.Section.Pages.Remove(target.Page);
        section.Pages.Remove(target);
        section.TouchUpdatedAt();

        if (ReferenceEquals(SelectedPage, target))
        {
            SelectedPage = section.Pages.Count == 0
                ? null
                : section.Pages[Math.Clamp(idx, 0, section.Pages.Count - 1)];
        }

        SaveNow();
        StatusMessage = $"页「{displayTitle}」已移入回收站（保留 {NoteTrashItem.DefaultRetentionDays} 天）";
    }

    // ---------- 回收站操作 ----------

    private void RestoreTrashItem(object? parameter)
    {
        if (parameter is not NoteTrashItemViewModel vm)
        {
            return;
        }

        var item = vm.Item;
        if (item.IsSection && item.Section is not null)
        {
            var restoredSec = item.Section.Clone();
            restoredSec.Normalize();
            if (Sections.Any(s => string.Equals(s.Id, restoredSec.Id, StringComparison.Ordinal)))
            {
                restoredSec.Id = Guid.NewGuid().ToString("N");
            }

            foreach (var p in restoredSec.Pages)
            {
                VditorAssetHost.SyncPageImagesToCache(p);
            }

            _services.Data.NoteSections.Add(restoredSec);
            var secVm = new NoteSectionItemViewModel(this, restoredSec);
            Sections.Add(secVm);
            RaisePropertyChanged(nameof(HasNoSections));

            _services.Data.NoteTrash.Remove(item);
            TrashItems.Remove(vm);
            RefreshTrashUI();

            SelectedSection = secVm;
            SelectedPage = secVm.Pages.FirstOrDefault();
            SaveNow();
            StatusMessage = $"已还原分区「{restoredSec.Name}」（含 {restoredSec.Pages.Count} 页）";
            return;
        }

        if (!item.IsSection && item.Page is not null)
        {
            var restoredPage = item.Page.Clone();
            restoredPage.Normalize();
            VditorAssetHost.SyncPageImagesToCache(restoredPage);

            // 优先放回原分区（按 Id 匹配，其次按名称匹配）；若原分区已被删除则自动重建
            var targetSec = Sections.FirstOrDefault(s =>
                    !string.IsNullOrWhiteSpace(item.OriginalSectionId)
                    && string.Equals(s.Id, item.OriginalSectionId, StringComparison.Ordinal))
                ?? Sections.FirstOrDefault(s =>
                    string.Equals(s.Name, item.OriginalSectionName, StringComparison.Ordinal));

            if (targetSec is null)
            {
                var recreated = new NoteSection
                {
                    Id = string.IsNullOrWhiteSpace(item.OriginalSectionId) ? Guid.NewGuid().ToString("N") : item.OriginalSectionId,
                    Name = string.IsNullOrWhiteSpace(item.OriginalSectionName) ? "已还原分区" : item.OriginalSectionName,
                    ColorHex = string.IsNullOrWhiteSpace(item.OriginalSectionColorHex) ? "#2F75B5" : item.OriginalSectionColorHex,
                };
                _services.Data.NoteSections.Add(recreated);
                targetSec = new NoteSectionItemViewModel(this, recreated);
                Sections.Add(targetSec);
                RaisePropertyChanged(nameof(HasNoSections));
            }

            if (targetSec.Pages.Any(p => string.Equals(p.Id, restoredPage.Id, StringComparison.Ordinal)))
            {
                restoredPage.Id = Guid.NewGuid().ToString("N");
            }

            targetSec.Section.Pages.Add(restoredPage);
            var pageVm = new NotePageItemViewModel(this, targetSec, restoredPage);
            targetSec.Pages.Add(pageVm);
            targetSec.TouchUpdatedAt();

            _services.Data.NoteTrash.Remove(item);
            TrashItems.Remove(vm);
            RefreshTrashUI();

            SelectedSection = targetSec;
            SelectedPage = pageVm;
            SaveNow();
            var title = string.IsNullOrWhiteSpace(restoredPage.Title) ? "无标题页" : restoredPage.Title;
            StatusMessage = $"已还原页「{title}」到分区「{targetSec.Name}」";
        }
    }

    private void DeleteTrashItemPermanently(object? parameter)
    {
        if (parameter is not NoteTrashItemViewModel vm)
        {
            return;
        }

        var ok = Confirm?.Invoke(
            "彻底删除",
            $"确定彻底删除「{vm.DisplayTitle}」吗？此操作不可撤销。") ?? false;
        if (!ok)
        {
            return;
        }

        _services.Data.NoteTrash.Remove(vm.Item);
        TrashItems.Remove(vm);
        RefreshTrashUI();
        SaveNow();
        VditorAssetHost.PruneUnreferencedCachedImages(_services.Data.NoteSections, _services.Data.NoteTrash);
        StatusMessage = $"已彻底删除「{vm.DisplayTitle}」";
    }

    private void EmptyTrash()
    {
        if (TrashItems.Count == 0)
        {
            return;
        }

        var count = TrashItems.Count;
        var ok = Confirm?.Invoke(
            "清空回收站",
            $"确定清空回收站中的全部 {count} 项内容吗？此操作不可撤销。") ?? false;
        if (!ok)
        {
            return;
        }

        _services.Data.NoteTrash.Clear();
        TrashItems.Clear();
        RefreshTrashUI();
        SaveNow();
        VditorAssetHost.PruneUnreferencedCachedImages(_services.Data.NoteSections, _services.Data.NoteTrash);
        StatusMessage = $"已清空回收站（共 {count} 项）";
    }

    private void RefreshTrashUI()
    {
        RaisePropertyChanged(
            nameof(HasTrashItems),
            nameof(HasNoTrashItems),
            nameof(TrashButtonText),
            nameof(TrashSummaryText));
        (EmptyTrashCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ---------- 编辑器插入与表格调色 ----------

    private void InsertDefaultTable()
    {
        EnsureActivePage("无标题页");
        InsertMarkdownRequested?.Invoke($"\n{NotePage.Default3x3MarkdownTable}\n\n");
        StatusMessage = "已插入 3×3 表格（支持在表格内新增/删除行列、拖拽与快捷键）";
    }

    private void PasteImageFromClipboard()
    {
        var image = NoteImageHelper.TryGetClipboardImage();
        if (image is null)
        {
            StatusMessage = "剪贴板里没有图片（在编辑区内按 Ctrl+V 可直接粘贴文字、表格或截图）";
            return;
        }

        var pageVm = EnsureActivePage("无标题页");
        var (base64, mime) = NoteImageHelper.EncodeBitmapSource(image);
        var key = VditorAssetHost.SanitizeImageKey($"img_{Guid.NewGuid():N}.png") ?? $"img_{Guid.NewGuid():N}.png";
        pageVm.Page.Images[key] = new NoteImageAsset { MimeType = mime, Base64 = base64 };
        VditorAssetHost.EnsureImageCached(key, base64);
        pageVm.TouchUpdatedAt();
        ScheduleSave();
        InsertMarkdownRequested?.Invoke($"\n![image]({NotePage.ImageVirtualHostPrefix}{key})\n");
        StatusMessage = $"已粘贴图片（{image.PixelWidth}×{image.PixelHeight}）";
    }

    private void InsertImageFromFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要插入的图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var (base64, mime) = NoteImageHelper.EncodeImageFile(dialog.FileName);
            var pageVm = EnsureActivePage("无标题页");
            var caption = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName)
                .Replace("[", string.Empty)
                .Replace("]", string.Empty);
            if (string.IsNullOrWhiteSpace(caption))
            {
                caption = "image";
            }

            var ext = mime.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || mime.Contains("jpg", StringComparison.OrdinalIgnoreCase)
                ? ".jpg"
                : ".png";
            var key = VditorAssetHost.SanitizeImageKey($"img_{Guid.NewGuid():N}{ext}") ?? $"img_{Guid.NewGuid():N}{ext}";
            pageVm.Page.Images[key] = new NoteImageAsset { MimeType = mime, Base64 = base64 };
            VditorAssetHost.EnsureImageCached(key, base64);
            pageVm.TouchUpdatedAt();
            ScheduleSave();
            InsertMarkdownRequested?.Invoke($"\n![{caption}]({NotePage.ImageVirtualHostPrefix}{key})\n");
            StatusMessage = $"已插入图片：{System.IO.Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            AppLog.Write("插入图片失败", ex);
            ShowMessage?.Invoke("插入图片失败", ex.Message);
        }
    }

    private void ChangeTableHeaderColor(object? parameter)
    {
        if (SelectedPage is null || parameter is not string hex)
        {
            return;
        }

        if (SelectedPage.ApplyTableColors(headerBg: hex, firstColBg: null, cellBg: null))
        {
            OnPropertyChanged(nameof(CurrentTableHeaderBgColor));
            TableColorsChangedRequested?.Invoke(SelectedPage);
            SaveNow();
            StatusMessage = $"已更新表格首行颜色为 {SelectedPage.TableHeaderBgColor}";
        }
    }

    private void ChangeTableFirstColumnColor(object? parameter)
    {
        if (SelectedPage is null || parameter is not string hex)
        {
            return;
        }

        if (SelectedPage.ApplyTableColors(headerBg: null, firstColBg: hex, cellBg: null))
        {
            OnPropertyChanged(nameof(CurrentTableFirstColumnBgColor));
            TableColorsChangedRequested?.Invoke(SelectedPage);
            SaveNow();
            StatusMessage = $"已更新表格首列颜色为 {SelectedPage.TableFirstColumnBgColor}";
        }
    }

    private void ChangeTableCellColor(object? parameter)
    {
        if (SelectedPage is null || parameter is not string hex)
        {
            return;
        }

        if (SelectedPage.ApplyTableColors(headerBg: null, firstColBg: null, cellBg: hex))
        {
            OnPropertyChanged(nameof(CurrentTableCellBgColor));
            TableColorsChangedRequested?.Invoke(SelectedPage);
            SaveNow();
            StatusMessage = $"已更新表格正文单元格颜色为 {SelectedPage.TableCellBgColor}";
        }
    }

    private void ResetTableColors()
    {
        if (SelectedPage is null)
        {
            return;
        }

        if (SelectedPage.ApplyTableColors(
            NoteTable.DefaultHeaderBgColor,
            NoteTable.DefaultFirstColumnBgColor,
            NoteTable.DefaultCellBgColor))
        {
            RaisePropertyChanged(
                nameof(CurrentTableHeaderBgColor),
                nameof(CurrentTableFirstColumnBgColor),
                nameof(CurrentTableCellBgColor));
            TableColorsChangedRequested?.Invoke(SelectedPage);
            SaveNow();
            StatusMessage = "已恢复默认表格配色（OneNote 蓝首行/首列）";
        }
    }

    // ---------- 内部辅助 ----------

    private NotePageItemViewModel? FindPageById(string pageId)
    {
        if (SelectedPage is not null && string.Equals(SelectedPage.Id, pageId, StringComparison.Ordinal))
        {
            return SelectedPage;
        }

        foreach (var sec in Sections)
        {
            foreach (var page in sec.Pages)
            {
                if (string.Equals(page.Id, pageId, StringComparison.Ordinal))
                {
                    return page;
                }
            }
        }

        return null;
    }

    private NoteSectionItemViewModel EnsureActiveSection()
    {
        if (SelectedSection is not null)
        {
            return SelectedSection;
        }

        var existing = Sections.FirstOrDefault();
        if (existing is not null)
        {
            SelectedSection = existing;
            return existing;
        }

        var sec = new NoteSection
        {
            Name = "默认分区",
            ColorHex = SectionColorPresets[0].Hex,
        };
        _services.Data.NoteSections.Add(sec);
        var vm = new NoteSectionItemViewModel(this, sec);
        Sections.Add(vm);
        RaisePropertyChanged(nameof(HasNoSections));
        SelectedSection = vm;
        return vm;
    }

    private NotePageItemViewModel EnsureActivePage(string defaultTitle)
    {
        if (SelectedPage is not null)
        {
            return SelectedPage;
        }

        var section = EnsureActiveSection();
        var existing = section.Pages.FirstOrDefault();
        if (existing is not null)
        {
            SelectedPage = existing;
            return existing;
        }

        var page = CreateBlankPage(defaultTitle, withDefaultTable: false);
        section.Section.Pages.Add(page);
        var vm = new NotePageItemViewModel(this, section, page);
        section.Pages.Add(vm);
        SelectedPage = vm;
        return vm;
    }

    private static NotePage CreateBlankPage(string title, bool withDefaultTable)
    {
        return new NotePage
        {
            Title = title,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
            Markdown = withDefaultTable ? NotePage.Default3x3MarkdownTable + "\n\n" : string.Empty,
            TableHeaderBgColor = NoteTable.DefaultHeaderBgColor,
            TableFirstColumnBgColor = NoteTable.DefaultFirstColumnBgColor,
            TableCellBgColor = NoteTable.DefaultCellBgColor,
            Blocks = new List<NoteBlock>(),
        };
    }

    private void RaiseSectionAndPageCommandsCanExecute()
    {
        (RenameSectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChangeSectionColorCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteSectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RenamePageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeletePageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChangeTableHeaderColorCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChangeTableFirstColumnColorCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChangeTableCellColorCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResetTableColorsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
