using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SinUpdateHub.App.ViewModels;

namespace SinUpdateHub.App.Views;

/// <summary>百度网盘浏览与文件夹下载页面。</summary>
public partial class BaiduPanView : UserControl
{
    public BaiduPanView()
    {
        InitializeComponent();
    }

    private async void DirBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is BaiduPanViewModel vm)
        {
            e.Handled = true;
            await vm.RefreshDirAsync();
        }
    }

    private async void FileListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is BaiduPanViewModel vm
            && FileListBox.SelectedItem is BaiduFileItemViewModel item
            && item.IsDir)
        {
            await vm.OpenFolderAsync(item);
        }
    }

    private async void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is BaiduPanViewModel vm
            && e.NewValue is BaiduFolderNodeViewModel node)
        {
            await vm.SelectFolderNodeAsync(node);
        }
    }
}
