<#
.SYNOPSIS
    启动 SinUpdateHub 并截取窗口图（用于开发期 UI 自检）。
.DESCRIPTION
    用 PrintWindow(PW_RENDERFULLCONTENT) 抓取窗口位图，即使窗口被遮挡也能截到。
.EXAMPLE
    pwsh -File tools\capture.ps1 -Exe dist\SinUpdateHub.exe -OutFile C:\Windows\Temp\shot.png
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [int]$WaitSeconds = 8,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

$Exe = [System.IO.Path]::GetFullPath($Exe)
$OutFile = [System.IO.Path]::GetFullPath($OutFile)

$code = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WindowShot
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    public static double GetScale(IntPtr hWnd)
    {
        uint dpi = GetDpiForWindow(hWnd);
        if (dpi == 0) dpi = 96;
        return dpi / 96.0;
    }

    public static string Capture(IntPtr hWnd, string path)
    {
        ShowWindow(hWnd, 9); // SW_RESTORE
        SetForegroundWindow(hWnd);
        System.Threading.Thread.Sleep(700);

        double scale = GetScale(hWnd);

        RECT rect;
        if (!GetWindowRect(hWnd, out rect))
        {
            return "GetWindowRect failed";
        }

        // If the host process is not DPI-aware, GetWindowRect returns virtualized (96dpi) coords.
        int w = (int)Math.Round((rect.Right - rect.Left) * scale);
        int h = (int)Math.Round((rect.Bottom - rect.Top) * scale);
        if (w <= 0 || h <= 0)
        {
            return "bad window size: " + w + "x" + h;
        }

        using (var bmp = new Bitmap(w, h))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(hWnd, hdc, 2); // PW_RENDERFULLCONTENT
                g.ReleaseHdc(hdc);
                if (!ok)
                {
                    g.CopyFromScreen((int)(rect.Left * scale), (int)(rect.Top * scale), 0, 0, new Size(w, h));
                }
            }
            bmp.Save(path, ImageFormat.Png);
        }
        return "ok " + w + "x" + h + " (scale " + scale.ToString("0.##") + ")";
    }
}
'@

Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing

$proc = Start-Process -FilePath $Exe -PassThru
Write-Host "started pid=$($proc.Id)"

$deadline = (Get-Date).AddSeconds($WaitSeconds)
$hwnd = [IntPtr]::Zero
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    $proc.Refresh()
    if ($proc.HasExited) { throw "进程已退出，退出码 $($proc.ExitCode)" }
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
        $hwnd = $proc.MainWindowHandle
        break
    }
}

if ($hwnd -eq [IntPtr]::Zero) {
    throw "等待窗口超时（$WaitSeconds 秒）"
}

Start-Sleep -Seconds 2
$result = [WindowShot]::Capture($hwnd, $OutFile)
Write-Host "capture: $result -> $OutFile"

if (-not $KeepRunning) {
    Start-Sleep -Milliseconds 500
    try { $proc.CloseMainWindow() | Out-Null } catch { }
    Start-Sleep -Milliseconds 800
    if (-not $proc.HasExited) { $proc.Kill() }
    Write-Host "process stopped"
} else {
    Write-Host "process kept running pid=$($proc.Id)"
}
