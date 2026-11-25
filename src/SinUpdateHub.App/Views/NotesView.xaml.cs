using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SinUpdateHub.App.ViewModels;

namespace SinUpdateHub.App.Views;

/// <summary>SinNote 页面交互（离线 Vditor IR 编辑器宿主、Ctrl+F/E 查找跳转、右键选中项）。</summary>
public partial class NotesView : UserControl
{
    private NotesViewModel? _subscribedVm;

    public NotesView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Unsubscribe();

        MarkdownEditor.ContentChanged += OnEditorContentChanged;
        MarkdownEditor.ShortcutRequested += OnEditorShortcutRequested;
        MarkdownEditor.EditorFocused += OnEditorFocused;
        MarkdownEditor.ImageSavedRequested += OnEditorImageSavedRequested;
        MarkdownEditor.TableColorsChanged += OnEditorTableColorsChanged;
        MarkdownEditor.FontFamilyChanged += OnEditorFontFamilyChanged;
        MarkdownEditor.ZoomChanged += OnEditorZoomChanged;
    }

    private NotesViewModel? Vm => DataContext as NotesViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Unsubscribe();
        if (DataContext is NotesViewModel vm)
        {
            _subscribedVm = vm;
            _subscribedVm.NavigateToMatchRequested += OnNavigateToMatchRequested;
            _subscribedVm.InsertMarkdownRequested += OnInsertMarkdownRequested;
            _subscribedVm.EditorReloadPageRequested += OnEditorReloadPageRequested;
            _subscribedVm.TableColorsChangedRequested += OnTableColorsChangedRequested;
            _subscribedVm.ClearSearchHighlightRequested += OnClearSearchHighlightRequested;
            _subscribedVm.FlushEditorRequested += OnFlushEditorRequested;
            _subscribedVm.TypographyChangedRequested += OnTypographyChangedRequested;

            MarkdownEditor.UpdateTypography(_subscribedVm.NoteFontFamily, _subscribedVm.NoteZoomPercent);
            MarkdownEditor.LoadPage(_subscribedVm.SelectedPage, force: true);
        }
    }

    private void Unsubscribe()
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.NavigateToMatchRequested -= OnNavigateToMatchRequested;
            _subscribedVm.InsertMarkdownRequested -= OnInsertMarkdownRequested;
            _subscribedVm.EditorReloadPageRequested -= OnEditorReloadPageRequested;
            _subscribedVm.TableColorsChangedRequested -= OnTableColorsChangedRequested;
            _subscribedVm.ClearSearchHighlightRequested -= OnClearSearchHighlightRequested;
            _subscribedVm.FlushEditorRequested -= OnFlushEditorRequested;
            _subscribedVm.TypographyChangedRequested -= OnTypographyChangedRequested;
            _subscribedVm = null;
        }
    }

    private void OnEditorContentChanged(string pageId, string markdown)
    {
        Vm?.UpdatePageMarkdownFromEditor(pageId, markdown);
    }

    private void OnEditorShortcutRequested(string key)
    {
        if (string.Equals(key, "F", StringComparison.OrdinalIgnoreCase))
        {
            FocusSearchBox(NoteSearchScope.CurrentPage);
        }
        else if (string.Equals(key, "E", StringComparison.OrdinalIgnoreCase))
        {
            FocusSearchBox(NoteSearchScope.AllSections);
        }
        else if (string.Equals(key, "S", StringComparison.OrdinalIgnoreCase))
        {
            Vm?.SaveNow();
        }
    }

    private void OnEditorFocused()
    {
        if (Vm is not null)
        {
            Vm.IsSearchDropdownOpen = false;
        }
    }

    private void OnEditorImageSavedRequested(string pageId, string imageId, string mimeType, string base64)
    {
        Vm?.RegisterPastedImage(pageId, imageId, mimeType, base64);
    }

    private void OnEditorTableColorsChanged(string headerBg, string firstColBg, string cellBg)
    {
        Vm?.UpdateTableColorsFromEditor(headerBg, firstColBg, cellBg);
    }

    private void OnEditorFontFamilyChanged(string fontFamily)
    {
        Vm?.UpdateFontFamilyFromEditor(fontFamily);
    }

    private void OnEditorZoomChanged(int zoomPercent)
    {
        Vm?.UpdateZoomFromEditor(zoomPercent);
    }

    private void OnTypographyChangedRequested(string fontFamily, int zoomPercent)
    {
        MarkdownEditor.UpdateTypography(fontFamily, zoomPercent);
    }

    private void OnInsertMarkdownRequested(string snippet)
    {
        MarkdownEditor.InsertMarkdown(snippet);
    }

    private void OnEditorReloadPageRequested(NotePageItemViewModel? page, bool force)
    {
        MarkdownEditor.LoadPage(page, force);
    }

    private void OnTableColorsChangedRequested(NotePageItemViewModel page)
    {
        MarkdownEditor.UpdateTableColors(page);
    }

    private void OnClearSearchHighlightRequested()
    {
        MarkdownEditor.ClearSearchHighlight();
    }

    private void OnFlushEditorRequested()
    {
        MarkdownEditor.FlushPendingEdits();
    }

    private void ListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
        }
    }

    /// <summary>供主窗口与本控件统一处理 Ctrl+F / Ctrl+E / Ctrl+S 快捷键。</summary>
    public void HandleShortcut(KeyEventArgs e)
    {
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        if (key == Key.F)
        {
            e.Handled = true;
            FocusSearchBox(NoteSearchScope.CurrentPage);
            return;
        }

        if (key == Key.E)
        {
            e.Handled = true;
            FocusSearchBox(NoteSearchScope.AllSections);
            return;
        }

        if (key == Key.S)
        {
            e.Handled = true;
            Vm?.FlushPendingSave();
        }
    }

    private void NotesView_PreviewKeyDown(object sender, KeyEventArgs e) => HandleShortcut(e);

    private void FocusSearchBox(NoteSearchScope scope)
    {
        Vm?.OpenSearch(scope);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Window.GetWindow(this)?.Activate();
            NoteSearchBox?.Focus();
            if (NoteSearchBox is not null)
            {
                Keyboard.Focus(NoteSearchBox);
                NoteSearchBox.SelectAll();
            }
        });
    }

    private void NoteSearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.IsSearchOpen = true;
        if (!string.IsNullOrWhiteSpace(Vm.SearchText) || Vm.IsAllSectionsScope)
        {
            Vm.IsSearchDropdownOpen = true;
        }
    }

    private void NoteSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (e.Key == Key.Enter || e.Key == Key.F3)
        {
            e.Handled = true;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            var cmd = shift ? Vm.PrevSearchMatchCommand : Vm.NextSearchMatchCommand;
            if (cmd.CanExecute(null))
            {
                cmd.Execute(null);
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Vm.CloseSearchCommand.Execute(null);
            MarkdownEditor.FocusEditor();
        }
    }

    private void NotesView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not null && Vm.IsSearchDropdownOpen
            && e.OriginalSource is DependencyObject src1
            && !IsDescendantOf(src1, SearchBoxContainer)
            && !IsDescendantOf(src1, SearchPopupBorder))
        {
            Vm.IsSearchDropdownOpen = false;
        }
    }

    private void OnNavigateToMatchRequested(NoteSearchResultItem item, bool focusTarget)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (item.IsTitleMatch)
            {
                MarkdownEditor.ClearSearchHighlight();
                PageTitleTextBox.BringIntoView();
                if (focusTarget)
                {
                    PageTitleTextBox.Focus();
                }

                var start = Math.Clamp(item.MatchIndex, 0, PageTitleTextBox.Text.Length);
                var len = Math.Clamp(item.MatchLength, 0, PageTitleTextBox.Text.Length - start);
                PageTitleTextBox.Select(start, len);
                return;
            }

            MarkdownEditor.HighlightSearch(Vm?.SearchText ?? string.Empty, item.OccurrenceIndex);
            if (focusTarget)
            {
                MarkdownEditor.FocusEditor();
            }
        });
    }

    private static bool IsDescendantOf(DependencyObject? current, DependencyObject? ancestor)
    {
        if (ancestor is null)
        {
            return false;
        }

        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
