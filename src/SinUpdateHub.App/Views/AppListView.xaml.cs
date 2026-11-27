using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SinUpdateHub.App.ViewModels;

namespace SinUpdateHub.App.Views;

/// <summary>软件列表/软件下载页的代码隐藏：管理表头三态排序、「更多操作」弹出及「软件下载」隐藏「包/新版本」列。</summary>
public partial class AppListView : UserControl
{
    private MainViewModel? _subscribedVm;

    public AppListView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += (_, _) => Unsubscribe();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !ReferenceEquals(_subscribedVm, vm))
        {
            Unsubscribe();
            _subscribedVm = vm;
            _subscribedVm.PageChanged += OnPageChanged;
        }

        UpdatePageColumns();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Unsubscribe();
        if (DataContext is MainViewModel vm)
        {
            _subscribedVm = vm;
            _subscribedVm.PageChanged += OnPageChanged;
        }

        UpdatePageColumns();
    }

    private void Unsubscribe()
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.PageChanged -= OnPageChanged;
            _subscribedVm = null;
        }
    }

    private void OnPageChanged(MainPage _) => UpdatePageColumns();

    private void UpdatePageColumns()
    {
        var isOfficial = (DataContext as MainViewModel)?.IsOfficialPage == true;
        var vis = isOfficial ? Visibility.Collapsed : Visibility.Visible;
        ColPackage.Visibility = vis;
        ColLatestVersion.Visibility = vis;
    }

    /// <summary>点击可排序表头（项目名称 / 来源 / 状态）时执行三态循环：升序 → 降序 → 恢复默认。</summary>
    private void EntriesGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var column = e.Column;
        if (!column.CanUserSort || string.IsNullOrWhiteSpace(column.SortMemberPath))
        {
            return;
        }

        ListSortDirection? nextDirection = column.SortDirection switch
        {
            null => ListSortDirection.Ascending,
            ListSortDirection.Ascending => ListSortDirection.Descending,
            _ => null,
        };

        foreach (var col in EntriesGrid.Columns)
        {
            col.SortDirection = null;
        }

        column.SortDirection = nextDirection;
        vm.ApplySort(nextDirection is null ? null : column.SortMemberPath, nextDirection);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
        {
            return;
        }

        var menu = button.ContextMenu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
