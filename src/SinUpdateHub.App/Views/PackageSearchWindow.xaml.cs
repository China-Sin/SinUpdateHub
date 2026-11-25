using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.App.Views;

/// <summary>「搜索 WinGet 包 ID」对话框：按关键词查本地 WinGet 索引，选中后回填包 ID。</summary>
public partial class PackageSearchWindow : Window, INotifyPropertyChanged
{
    private readonly Func<string, Task<IReadOnlyList<WingetPackageInfo>>> _search;
    private string _keyword;
    private string _statusText = string.Empty;
    private bool _isSearching;

    public PackageSearchWindow(string keyword, Func<string, Task<IReadOnlyList<WingetPackageInfo>>> search)
    {
        InitializeComponent();

        _keyword = keyword ?? string.Empty;
        _search = search ?? throw new ArgumentNullException(nameof(search));
        DataContext = this;

        Loaded += async (_, _) => await SearchAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<WingetPackageInfo> Results { get; } = new();

    public string Keyword
    {
        get => _keyword;
        set
        {
            if (_keyword == value)
            {
                return;
            }

            _keyword = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatusText));
        }
    }

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (_isSearching == value)
            {
                return;
            }

            _isSearching = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotSearching));
        }
    }

    public bool IsNotSearching => !IsSearching;

    public WingetPackageInfo? SelectedPackage => ResultsList.SelectedItem as WingetPackageInfo;

    public async Task SearchAsync()
    {
        if (IsSearching)
        {
            return;
        }

        var keyword = (Keyword ?? string.Empty).Trim();
        if (keyword.Length == 0)
        {
            StatusText = "请输入软件名称或关键词（如 UU、微信、7zip、NetEase）。";
            return;
        }

        IsSearching = true;
        StatusText = "正在搜索 WinGet 包（首次使用需要先下载约 21 MB 的本地索引）…";
        Results.Clear();
        try
        {
            var list = await _search(keyword);
            foreach (var item in list)
            {
                Results.Add(item);
            }

            if (Results.Count > 0)
            {
                ResultsList.SelectedIndex = 0;
                StatusText = $"找到 {Results.Count} 个包，选中一行后点「确定」。";
            }
            else
            {
                StatusText = $"没有匹配「{keyword}」的包。试试更短的关键词，例如「UU」而不是「UU远程官方版」。";
            }
        }
        catch (Exception ex)
        {
            StatusText = "搜索失败：" + ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async void BtnSearch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SearchAsync();
        }
        catch (Exception ex)
        {
            StatusText = "搜索失败：" + ex.Message;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPackage is null)
        {
            MessageBox.Show(this, "请先选中一个包。", "SinUpdateHub", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

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

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
