<#
.SYNOPSIS
    SinUpdateHub UI 自动化自检（V2：内嵌页面 + 表格列表 + WinGet 包搜索）。
.DESCRIPTION
    - 用 Win32 EnumWindows 找窗口（UIA 的 RootElement 枚举不到 Modal 子窗口）
    - 用 UI Automation 点按钮（XAML 里关键按钮已设置 AutomationProperties.Name）
    - 用 PrintWindow(PW_RENDERFULLCONTENT) 按真实 DPI 抓图
    自检脚本，不影响交付程序。
.EXAMPLE
    powershell -NoProfile -File tools\ui-test.ps1 -Exe src\SinUpdateHub.App\bin\Debug\net8.0-windows\SinUpdateHub.exe -OutDir C:\Windows\Temp\uitest
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [switch]$KeepRunning,
    [switch]$CheckUpdates
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$Exe = [System.IO.Path]::GetFullPath($Exe)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$nativeCode = @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class UiNative
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("shcore.dll")] public static extern int GetProcessDpiAwareness(IntPtr hProcess, out int awareness);

    public const uint WM_CLOSE = 0x0010;

    static double GetScale(IntPtr hWnd)
    {
        int awareness = 0;
        try { GetProcessDpiAwareness(IntPtr.Zero, out awareness); } catch { awareness = 0; }
        uint dpi = GetDpiForWindow(hWnd);
        if (dpi == 0) dpi = 96;
        return awareness == 0 ? dpi / 96.0 : 1.0;
    }

    public static List<IntPtr> WindowsOf(uint targetPid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == targetPid) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string TitleOf(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hWnd, sb, 512);
        return sb.ToString();
    }

    public static bool Visible(IntPtr hWnd) { return IsWindowVisible(hWnd); }

    public static void CloseWindow(IntPtr hWnd) { PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }

    public static string Capture(IntPtr hWnd, string path)
    {
        ShowWindow(hWnd, 9);
        SetForegroundWindow(hWnd);
        System.Threading.Thread.Sleep(450);

        double scale = GetScale(hWnd);
        RECT r;
        if (!GetWindowRect(hWnd, out r)) return "GetWindowRect failed";
        int w = (int)Math.Round((r.Right - r.Left) * scale);
        int h = (int)Math.Round((r.Bottom - r.Top) * scale);
        if (w <= 0 || h <= 0) return "bad size " + w + "x" + h;

        using (var bmp = new Bitmap(w, h))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(hWnd, hdc, 2);
                g.ReleaseHdc(hdc);
                if (!ok) g.CopyFromScreen((int)(r.Left * scale), (int)(r.Top * scale), 0, 0, new Size(w, h));
            }
            bmp.Save(path, ImageFormat.Png);
        }
        return "ok " + w + "x" + h;
    }
}
'@
Add-Type -TypeDefinition $nativeCode -ReferencedAssemblies System.Drawing

function Wait-Window([int]$processId, [string]$titleLike, [int]$timeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($h in [UiNative]::WindowsOf([uint32]$processId)) {
            if (-not [UiNative]::Visible($h)) { continue }
            $title = [UiNative]::TitleOf($h)
            if ($title -and $title -like "*$titleLike*") { return $h }
        }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}

function Get-Elements([IntPtr]$hwnd, $controlType) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    if ($null -eq $root) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-Button([IntPtr]$hwnd, [string]$keyword) {
    foreach ($b in (Get-Elements $hwnd ([System.Windows.Automation.ControlType]::Button))) {
        try { if ($b.Current.Name -like "*$keyword*") { return $b } } catch { }
    }
    return $null
}

function Click-Button([IntPtr]$hwnd, [string]$keyword, [string]$label) {
    $btn = Find-Button $hwnd $keyword
    if ($null -eq $btn) {
        Write-Host "  [!] 没找到按钮：$keyword"
        return $false
    }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "  [+] 已点击：$label"
    return $true
}

function Save-Shot([IntPtr]$hwnd, [string]$fileName) {
    $path = Join-Path $OutDir $fileName
    $r = [UiNative]::Capture($hwnd, $path)
    Write-Host "  [shot] $fileName -> $r"
}

function Close-Window([IntPtr]$hwnd) {
    [UiNative]::CloseWindow($hwnd)
    Start-Sleep -Milliseconds 700
}

function Set-FirstEdit([IntPtr]$hwnd, [string]$value) {
    $edits = Get-Elements $hwnd ([System.Windows.Automation.ControlType]::Edit)
    if ($edits.Count -ge 1) {
        $edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
        return $true
    }
    return $false
}

$proc = Start-Process -FilePath $Exe -PassThru
Write-Host "started pid=$($proc.Id)  exe=$Exe"

try {
    $main = Wait-Window $proc.Id "SinUpdateHub" 30
    if ($main -eq [IntPtr]::Zero) { throw "主窗口没有出现" }
    Start-Sleep -Seconds 2
    Save-Shot $main "01-main.png"

    if ($CheckUpdates) {
        Write-Host "--- 检查全部更新 ---"
        if (Click-Button $main "检查全部更新" "检查全部更新") {
            Start-Sleep -Seconds 8
            Save-Shot $main "01b-main-checking.png"
            Start-Sleep -Seconds 12
            Save-Shot $main "01c-main-checked.png"
        }
    }

    Write-Host "--- 备份与恢复页面 ---"
    if (Click-Button $main "备份与恢复" "备份与恢复") {
        Start-Sleep -Milliseconds 1200
        Save-Shot $main "02-backup-page.png"
    }

    Write-Host "--- 设置页面 + 测试网络 ---"
    if (Click-Button $main "设置" "设置") {
        Start-Sleep -Milliseconds 1500
        Save-Shot $main "03-settings-page.png"
        if (Click-Button $main "测试网络" "测试网络") {
            Start-Sleep -Seconds 8
            Save-Shot $main "03b-settings-network.png"
        }
    }

    Write-Host "--- 回到软件列表 ---"
    if (Click-Button $main "软件列表" "软件列表") {
        Start-Sleep -Milliseconds 900
        Save-Shot $main "04-apps-page.png"
    }

    Write-Host "--- 添加软件：WinGet 自动识别 + 搜索包 ID ---"
    if (Click-Button $main "添加软件" "添加软件") {
        $editor = Wait-Window $proc.Id "添加软件" 10
        if ($editor -ne [IntPtr]::Zero) {
            Start-Sleep -Milliseconds 900
            if (Set-FirstEdit $editor "UU远程") { Write-Host "  已填项目名称：UU远程" }

            # 第二个 RadioButton = WinGet 来源
            $radios = Get-Elements $editor ([System.Windows.Automation.ControlType]::RadioButton)
            Write-Host "  单选框数量：$($radios.Count)"
            if ($radios.Count -ge 2) {
                $radios[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Write-Host "  已切换来源：WinGet"
            }
            Start-Sleep -Milliseconds 600

            if (Click-Button $editor "自动识别" "自动识别") {
                Start-Sleep -Seconds 5
                Save-Shot $editor "05-editor-autodetect.png"
            }

            if (Click-Button $editor "搜索包 ID" "搜索包 ID") {
                $picker = Wait-Window $proc.Id "搜索 WinGet 包" 25
                if ($picker -ne [IntPtr]::Zero) {
                    Start-Sleep -Seconds 3
                    Save-Shot $picker "06-package-search.png"
                    [void](Click-Button $picker "取消" "取消（包搜索）")
                } else {
                    Write-Host "  [!] 包搜索窗口没出现"
                }
            }

            [void](Click-Button $editor "取消" "取消（编辑）")
        } else {
            Write-Host "  [!] 添加软件窗口没出现"
        }
    }

    Write-Host "=== UI 自检完成，截图目录：$OutDir ==="
}
finally {
    Start-Sleep -Milliseconds 500
    if (-not $KeepRunning) {
        try { $proc.CloseMainWindow() | Out-Null } catch { }
        Start-Sleep -Milliseconds 800
        if (-not $proc.HasExited) { $proc.Kill() }
        Write-Host "process stopped"
    } else {
        Write-Host "process kept running pid=$($proc.Id)"
    }
}
