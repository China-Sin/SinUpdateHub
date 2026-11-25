using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SinUpdateHub.App.Services;
using SinUpdateHub.Core.Services;

namespace SinUpdateHub.App;

/// <summary>应用入口：初始化服务、全局异常保护、单实例运行。</summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：重复启动直接激活已有窗口
        _singleInstanceMutex = new Mutex(true, "SinUpdateHub.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("SinUpdateHub 已经在运行了。", "SinUpdateHub", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 全局异常兜底：写日志 + 弹窗，不让程序静默退出
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Write("未处理异常（AppDomain）", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write("未观察的任务异常", args.Exception);
            args.SetObserved();
        };

        try
        {
            AppServices.Initialize();
            AppLog.Write("=== SinUpdateHub 启动 ===");
        }
        catch (Exception ex)
        {
            AppLog.Write("初始化失败", ex);
            MessageBox.Show("初始化失败：" + ex.Message + "\n\n详情见日志：" + AppPaths.LogFile, "SinUpdateHub",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 剪贴板被其他软件占用时，WPF 原生 Cut/Copy/Paste 与焦点检查都会内部阻塞重试 1 秒以上；
        // 统一接管 TextBox 的 Paste CanExecute 与 Cut/Copy/Paste 执行，改为毫秒级非阻塞 Win32 剪贴板。
        EventManager.RegisterClassHandler(
            typeof(TextBox),
            CommandManager.PreviewCanExecuteEvent,
            new CanExecuteRoutedEventHandler(OnTextBoxPreviewCanExecute));
        EventManager.RegisterClassHandler(
            typeof(TextBox),
            CommandManager.PreviewExecutedEvent,
            new ExecutedRoutedEventHandler(OnTextBoxPreviewExecuted));
    }

    private static void OnTextBoxPreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || sender is not TextBox tb)
        {
            return;
        }

        e.CanExecute = tb.IsEnabled && !tb.IsReadOnly && Win32Clipboard.HasTextFormat();
        e.Handled = true;
    }

    private static void OnTextBoxPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is not TextBox tb || !tb.IsEnabled)
        {
            return;
        }

        if (e.Command == ApplicationCommands.Paste)
        {
            if (tb.IsReadOnly || !Win32Clipboard.TryGetText(out var pasteText))
            {
                return;
            }

            if (!tb.AcceptsReturn)
            {
                var lineBreak = pasteText.IndexOfAny(new[] { '\r', '\n' });
                if (lineBreak >= 0)
                {
                    pasteText = pasteText[..lineBreak];
                }
            }

            if (tb.MaxLength > 0)
            {
                var available = Math.Max(0, tb.MaxLength - (tb.Text.Length - tb.SelectionLength));
                if (pasteText.Length > available)
                {
                    pasteText = pasteText[..available];
                }
            }

            using (tb.DeclareChangeBlock())
            {
                var caretPos = tb.SelectionStart;
                tb.SelectedText = pasteText;
                tb.Select(caretPos + pasteText.Length, 0);
            }

            e.Handled = true;
            return;
        }

        if (e.Command != ApplicationCommands.Cut && e.Command != ApplicationCommands.Copy)
        {
            return;
        }

        if (tb.SelectionLength == 0 || (e.Command == ApplicationCommands.Cut && tb.IsReadOnly))
        {
            return;
        }

        Win32Clipboard.TrySetText(tb.SelectedText);

        if (e.Command == ApplicationCommands.Cut)
        {
            tb.SelectedText = string.Empty;
        }

        e.Handled = true;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("未处理异常（UI线程）", e.Exception);
        MessageBox.Show("发生未处理的错误：\n" + e.Exception.Message + "\n\n已写入日志：" + AppPaths.LogFile,
            "SinUpdateHub", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // ignore
        }

        _singleInstanceMutex?.Dispose();
        try
        {
            AppServices.Current?.Network?.Dispose();
        }
        catch
        {
            // ignore
        }

        base.OnExit(e);
    }
}
