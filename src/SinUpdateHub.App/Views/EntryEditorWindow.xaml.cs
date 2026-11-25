using System.Windows;
using System.Windows.Input;
using SinUpdateHub.App.ViewModels;

namespace SinUpdateHub.App.Views;

/// <summary>添加/编辑软件条目的对话框。</summary>
public partial class EntryEditorWindow : Window
{
    private readonly EntryEditorViewModel _viewModel;

    public EntryEditorWindow(EntryEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Loaded += async (_, _) =>
        {
            viewModel.Name = viewModel.Name; // 触发一次刷新，确保输入框绑定生效
            await viewModel.RefreshExeVersionAsync();
        };
    }

    private void OnRequestClose(bool saved)
    {
        try
        {
            DialogResult = saved;
        }
        catch
        {
            Close();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // ignore
            }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
