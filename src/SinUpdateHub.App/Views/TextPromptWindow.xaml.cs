using System.Windows;
using System.Windows.Input;

namespace SinUpdateHub.App.Views;

/// <summary>轻量深色文本输入弹窗（用于新建/重命名分区或页）。</summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string promptLabel, string initialValue = "")
    {
        InitializeComponent();

        Title = title;
        TitleTextBlock.Text = title;
        PromptLabelTextBlock.Text = promptLabel;
        InputTextBox.Text = initialValue ?? string.Empty;

        Loaded += (_, _) =>
        {
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        };
    }

    public string ResultText { get; private set; } = string.Empty;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var text = (InputTextBox.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            InputTextBox.Focus();
            return;
        }

        ResultText = text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
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
}
