using System.ComponentModel;
using System.Diagnostics;
using SinUpdateHub.Core.Abstractions;
using SinUpdateHub.Core.Models;

namespace SinUpdateHub.Core.Services;

/// <summary>
/// 安装器启动：
/// - .msi → msiexec /i；
/// - .exe → ShellExecute 启动（UAC 提权时加 runas）；
/// - .zip/.7z/.rar → 打开资源管理器并选中文件，提示手动解压；
/// - .msix/.appx 系列 → 暂不支持自动安装，提示手动双击。
/// 普通权限启动时会等待安装器退出（最多 30 分钟）并回传退出码；提权启动因句柄限制直接返回成功。
/// </summary>
public sealed class InstallerRunner : IInstallerRunner
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 创建安装器启动器。
    /// <paramref name="settingsAccessor"/> 仅为兼容 WPF 侧"所有服务统一注入设置"的构造方式而保留：
    /// 是否以管理员身份启动由 <see cref="RunAsync"/> 的 runAsAdmin 参数显式决定，这里不读取设置。
    /// </summary>
    public InstallerRunner(Func<AppSettings>? settingsAccessor = null)
    {
        // 预留：后续如需按设置里的下载/安装选项做默认值，可在此保存访问器。
        _ = settingsAccessor;
    }

    /// <summary>启动安装器。</summary>
    public async Task<InstallerResult> RunAsync(
        string filePath,
        string? arguments,
        bool runAsAdmin,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Failure("安装器路径为空，请重新检查更新后再试");
        }

        var path = Environment.ExpandEnvironmentVariables(filePath.Trim().Trim('"'));
        if (!File.Exists(path))
        {
            return Failure($"安装器文件不存在：{path}（可能已被删除或移动），请重新下载");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".msi":
                return await RunMsiAsync(path, arguments, runAsAdmin, ct).ConfigureAwait(false);

            case ".exe":
                return await RunExeAsync(path, arguments, runAsAdmin, ct).ConfigureAwait(false);

            case ".msix":
            case ".msixbundle":
            case ".appx":
            case ".appxbundle":
                return await RunAppxAsync(path, arguments, ct).ConfigureAwait(false);

            case ".zip":
            case ".7z":
            case ".rar":
                return OpenArchiveLocation(path);

            default:
                return Failure($"不认识的安装包类型「{extension}」，请手动打开处理：{path}");
        }
    }

    private static async Task<InstallerResult> RunMsiAsync(
        string path,
        string? arguments,
        bool runAsAdmin,
        CancellationToken ct)
    {
        var msiArguments = $"/i \"{path}\"";
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            msiArguments += " " + arguments.Trim();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = msiArguments,
            UseShellExecute = true,
        };
        if (runAsAdmin)
        {
            startInfo.Verb = "runas";
        }

        return await LaunchAsync(startInfo, $"msiexec.exe {msiArguments}", runAsAdmin, ct).ConfigureAwait(false);
    }

    private static async Task<InstallerResult> RunExeAsync(
        string path,
        string? arguments,
        bool runAsAdmin,
        CancellationToken ct)
    {
        var exeArguments = arguments?.Trim() ?? string.Empty;
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            Arguments = exeArguments,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
        };
        if (runAsAdmin)
        {
            startInfo.Verb = "runas";
        }

        var commandLine = exeArguments.Length == 0 ? $"\"{path}\"" : $"\"{path}\" {exeArguments}";
        return await LaunchAsync(startInfo, commandLine, runAsAdmin, ct).ConfigureAwait(false);
    }

    internal static bool IsSilentAppxArgs(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        var tokens = arguments.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(t =>
            t.Equals("/S", StringComparison.OrdinalIgnoreCase)
            || t.Equals("/SILENT", StringComparison.OrdinalIgnoreCase)
            || t.Equals("/QUIET", StringComparison.OrdinalIgnoreCase)
            || t.Equals("-QUIET", StringComparison.OrdinalIgnoreCase)
            || t.Equals("-SILENT", StringComparison.OrdinalIgnoreCase)
            || t.Equals("/Q", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<InstallerResult> RunAppxAsync(string path, string? arguments, CancellationToken ct)
    {
        if (IsSilentAppxArgs(arguments))
        {
            var escaped = path.Replace("'", "''");
            var psArgs = $"-NoProfile -ExecutionPolicy Bypass -Command \"Add-AppxPackage -Path '{escaped}'\"";
            var commandLine = $"powershell.exe {psArgs}";
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = psArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return Failure("无法启动 PowerShell 执行 MSIX/Appx 静默安装");
                }

                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(WaitTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return new InstallerResult
                    {
                        Launched = true,
                        Error = "MSIX/Appx 静默安装超过 30 分钟，已停止等待（安装可能仍在后台进行）",
                        CommandLine = commandLine,
                    };
                }

                var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
                var exitCode = process.ExitCode;
                return new InstallerResult
                {
                    Launched = true,
                    WaitedForExit = true,
                    ExitCode = exitCode,
                    CommandLine = commandLine,
                    Error = exitCode == 0
                        ? null
                        : string.IsNullOrWhiteSpace(stderr)
                            ? $"MSIX/Appx 静默安装失败（退出码 {exitCode}）"
                            : $"MSIX/Appx 静默安装失败（退出码 {exitCode}）：{stderr}",
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write($"启动 MSIX/Appx 静默安装失败：{commandLine}", ex);
                return Failure($"无法启动 MSIX/Appx 静默安装：{ex.Message}。请尝试手动双击安装：{path}");
            }
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            return new InstallerResult
            {
                Launched = true,
                WaitedForExit = false,
                NeedsManualAction = false,
                CommandLine = $"\"{path}\"",
            };
        }
        catch (Win32Exception ex)
        {
            AppLog.Write($"启动 MSIX/Appx 安装程序失败：{path}", ex);
            return Failure($"无法启动 MSIX/Appx 安装程序：{ex.Message}。请尝试手动双击安装：{path}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"启动 MSIX/Appx 安装程序异常：{path}", ex);
            return Failure($"无法启动 MSIX/Appx 安装程序：{ex.Message}。请尝试手动双击安装：{path}");
        }
    }

    /// <summary>压缩包不自动解压：打开资源管理器定位到文件，由用户手动处理。</summary>
    private static InstallerResult OpenArchiveLocation(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            return new InstallerResult
            {
                Launched = true,
                NeedsManualAction = true,
                CommandLine = $"explorer.exe /select,\"{path}\"（压缩包已打开所在目录，请手动解压后使用）",
            };
        }
        catch (Exception ex)
        {
            AppLog.Write($"打开资源管理器失败：{path}", ex);
            return Failure($"无法打开文件所在目录：{ex.Message}，请手动打开 {path}");
        }
    }

    private static async Task<InstallerResult> LaunchAsync(
        ProcessStartInfo startInfo,
        string commandLine,
        bool runAsAdmin,
        CancellationToken ct)
    {
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new InstallerResult
            {
                Launched = false,
                Error = "已取消管理员授权（UAC），安装未启动",
                CommandLine = commandLine,
            };
        }
        catch (Win32Exception ex)
        {
            AppLog.Write($"启动安装器失败：{commandLine}", ex);
            return new InstallerResult
            {
                Launched = false,
                Error = $"启动安装器失败：{ex.Message}。请尝试手动运行：{commandLine}",
                CommandLine = commandLine,
            };
        }
        catch (Exception ex)
        {
            AppLog.Write($"启动安装器异常：{commandLine}", ex);
            return new InstallerResult
            {
                Launched = false,
                Error = $"启动安装器失败：{ex.Message}",
                CommandLine = commandLine,
            };
        }

        if (process is null)
        {
            return new InstallerResult
            {
                Launched = false,
                Error = "启动安装器失败：系统没有返回进程句柄",
                CommandLine = commandLine,
            };
        }

        using (process)
        {
            if (runAsAdmin)
            {
                // UAC 提权后的进程由系统代理，拿不到可等待的退出码，直接视为已启动
                return new InstallerResult { Launched = true, CommandLine = commandLine };
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WaitTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new InstallerResult
                {
                    Launched = true,
                    Error = "安装器运行超过 30 分钟，已停止等待（安装可能仍在后台进行）",
                    CommandLine = commandLine,
                };
            }
            catch (Exception ex)
            {
                AppLog.Write($"等待安装器退出失败：{commandLine}", ex);
                return new InstallerResult
                {
                    Launched = true,
                    Error = $"无法等待安装器退出：{ex.Message}",
                    CommandLine = commandLine,
                };
            }

            int? exitCode = null;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (Exception ex)
            {
                AppLog.Write($"读取安装器退出码失败：{commandLine}", ex);
            }

            return new InstallerResult
            {
                Launched = true,
                WaitedForExit = true,
                ExitCode = exitCode,
                CommandLine = commandLine,
                Error = DescribeExitCode(exitCode),
            };
        }
    }

    /// <summary>0 / 3010（需重启）/ 1641（已重启）都算安装流程正常结束，其余给出中文提示。</summary>
    private static string? DescribeExitCode(int? exitCode) => exitCode switch
    {
        null or 0 or 3010 or 1641 => null,
        _ => $"安装器退出码 {exitCode}，可能未成功安装或用户取消了安装，请手动确认",
    };

    private static InstallerResult Failure(string message) => new()
    {
        Launched = false,
        Error = message,
    };
}
