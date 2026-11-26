using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SinUpdateHub.App.ViewModels;
using SinUpdateHub.App.Views;
using SinUpdateHub.Core.Models;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App.Views;

/// <summary>主窗口外壳：左侧导航 + 右侧内容区（软件列表 / 备份与恢复 / 设置，都是内嵌页面）。</summary>
public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _services = AppServices.Current;
        _viewModel = new MainViewModel(_services)
        {
            EditEntryRequested = ShowEntryEditor,
            ChooseAssetRequested = ShowAssetPicker,
            ShowMessage = (title, message) =>
                MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information),
            Confirm = (title, message) =>
                MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes,
        };
        _viewModel.NotesPage.PromptText = ShowTextPrompt;
        _viewModel.NotesPage.PreviewImage = ShowImagePreview;
        _viewModel.NotesPage.OpenTrashRequested = ShowNoteTrash;
        _viewModel.BaiduPanPage.AuthorizeBaiduRequested = ShowBaiduAuth;
        _viewModel.BaiduPanPage.PickFolderRequested = ShowFolderPicker;
        _viewModel.BackupPage.SelectRestoreItems = ShowRestoreSelection;

        DataContext = _viewModel;

        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        }
        catch
        {
            // 图标加载失败不影响使用
        }

        RestoreWindowState();

        SourceInitialized += (_, _) => AttachMaximizedWorkAreaHook();
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closing += (_, _) =>
        {
            _viewModel.NotesPage.FlushPendingSave();
            SaveWindowState();
        };
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsNotesPage)
        {
            return;
        }

        // 仅当焦点位于 SinNote 视图外部（如左侧导航栏）时转发 Ctrl+F / Ctrl+E / Ctrl+V
        if (e.OriginalSource is DependencyObject source)
        {
            var current = source;
            while (current is not null)
            {
                if (ReferenceEquals(current, NotesPageView))
                {
                    return;
                }

                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
        }

        NotesPageView.HandleShortcut(e);
    }

    private void RestoreWindowState()
    {
        var s = _services.Settings;

        // 表格式列表需要更宽的空间：旧版本保存的 1180 这类窄尺寸不再沿用，改用 XAML 默认宽度
        if (s.WindowWidth >= 1200)
        {
            Width = s.WindowWidth;
        }

        if (s.WindowHeight >= 660)
        {
            Height = s.WindowHeight;
        }

        if (s.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowState()
    {
        try
        {
            var s = _services.Settings;
            if (WindowState == WindowState.Normal)
            {
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            }

            s.WindowMaximized = WindowState == WindowState.Maximized;
            _services.SaveSettings();
        }
        catch
        {
            // ignore
        }
    }

    private AppEntry? ShowEntryEditor(AppEntry? existing, SourceKind defaultSource)
    {
        var vm = new EntryEditorViewModel(_services, existing, defaultSource)
        {
            SearchWingetPackagesRequested = keyword => _services.WingetCatalog.SearchAsync(keyword),
            PickWingetPackageRequested = ShowPackageSearch,
        };

        var window = new EntryEditorWindow(vm) { Owner = this };
        return window.ShowDialog() == true ? vm.Result : null;
    }

    private WingetPackageInfo? ShowPackageSearch(string keyword)
    {
        var window = new PackageSearchWindow(
            keyword,
            kw => _services.WingetCatalog.SearchAsync(kw)) { Owner = this };

        return window.ShowDialog() == true ? window.SelectedPackage : null;
    }

    private AssetCandidate? ShowAssetPicker(AvailableUpdate update)
    {
        var window = new AssetPickerWindow(update) { Owner = this };
        return window.ShowDialog() == true ? window.SelectedCandidate : null;
    }

    private string? ShowTextPrompt(string title, string label, string initialValue)
    {
        var window = new TextPromptWindow(title, label, initialValue) { Owner = this };
        return window.ShowDialog() == true ? window.ResultText : null;
    }

    private void ShowImagePreview(BitmapSource image, string? caption, string? base64)
    {
        var window = new ImagePreviewWindow(image, caption, base64) { Owner = this };
        window.ShowDialog();
    }

    private void ShowNoteTrash()
    {
        var window = new NoteTrashWindow(_viewModel.NotesPage) { Owner = this };
        window.ShowDialog();
    }

    private string? ShowBaiduAuth(string appKey)
    {
        var window = new BaiduAuthWindow(appKey) { Owner = this };
        return window.ShowDialog() == true ? window.AuthCode : null;
    }

    private string? ShowFolderPicker(string initialDirectory)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择百度网盘下载保存目录",
            InitialDirectory = System.IO.Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private RestoreSelectionResult? ShowRestoreSelection(string sourceName, AppData incomingData)
    {
        var window = new RestoreSelectionWindow(sourceName, incomingData) { Owner = this };
        return window.ShowDialog() == true ? window.Result : null;
    }

    // ---------- 自绘标题栏 ----------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
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
                // 拖拽过程中窗口状态变化会抛异常，忽略
            }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---------- 无边框窗口最大化精确贴合工作区（防止底部被任务栏/开始菜单遮挡） ----------

    private void AttachMaximizedWorkAreaHook()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && HwndSource.FromHwnd(handle) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ApplyWorkAreaMinMaxInfo(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ApplyWorkAreaMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var rcWork = monitorInfo.rcWork;
        var rcMonitor = monitorInfo.rcMonitor;

        mmi.ptMaxPosition.x = Math.Abs(rcWork.left - rcMonitor.left);
        mmi.ptMaxPosition.y = Math.Abs(rcWork.top - rcMonitor.top);
        mmi.ptMaxSize.x = Math.Abs(rcWork.right - rcWork.left);
        mmi.ptMaxSize.y = Math.Abs(rcWork.bottom - rcWork.top);

        Marshal.StructureToPtr(mmi, lParam, fDeleteOld: true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
