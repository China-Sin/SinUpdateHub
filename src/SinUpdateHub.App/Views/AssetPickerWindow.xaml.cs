using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.App.Views;

/// <summary>当 GitHub Assets 匹配到多个安装包时，让用户选一个。</summary>
public partial class AssetPickerWindow : Window
{
    private readonly ObservableCollection<AssetCandidate> _candidates = new();

    public AssetPickerWindow(AvailableUpdate update)
    {
        InitializeComponent();

        VersionText = string.IsNullOrWhiteSpace(update.Version) ? "（未知版本）" : update.Version;
        foreach (var candidate in update.Candidates)
        {
            _candidates.Add(candidate);
        }

        CandidateList.ItemsSource = _candidates;
        if (_candidates.Count > 0)
        {
            CandidateList.SelectedIndex = 0;
        }

        DataContext = this;
    }

    public string VersionText { get; }

    public string HintText => "请选择要下载的安装包（列表来自本次检查解析到的全部候选文件）";

    public AssetCandidate? SelectedCandidate => CandidateList.SelectedItem as AssetCandidate;

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

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is null)
        {
            MessageBox.Show(this, "请先选择一个安装包。", "SinUpdateHub", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            DialogResult = true;
        }
        catch
        {
            Close();
        }
    }
}
