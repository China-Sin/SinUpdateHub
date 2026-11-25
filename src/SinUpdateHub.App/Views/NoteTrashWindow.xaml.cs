using System.Windows;
using System.Windows.Input;
using SinUpdateHub.App.ViewModels;

namespace SinUpdateHub.App.Views;

/// <summary>SinNote 回收站弹窗：查看最近删除的分区与笔记页，支持一键还原或彻底删除。</summary>
public partial class NoteTrashWindow : Window
{
    public NoteTrashWindow(NotesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
