<#
.SYNOPSIS
    一键部署 SinUpdateHub：关闭正在运行的实例 → 发布最新单文件 exe → 替换交付物 → 刷新源码/说明/截图 → 可选重新启动。
.DESCRIPTION
    以后版本更新只用跑这一个脚本，不需要人工关软件或手动复制文件。
    - 优雅关闭（CloseMainWindow），超时后强制结束；
    - 默认会重新执行 publish，-SkipPublish 则复用 dist 里已有的 exe；
    - 原来有实例在跑时，部署完会自动重新启动。
.EXAMPLE
    powershell -NoProfile -File tools\deploy.ps1
    powershell -NoProfile -File tools\deploy.ps1 -SkipPublish
    powershell -NoProfile -File tools\deploy.ps1 -Relaunch
#>
param(
    [switch]$SkipPublish,
    [switch]$Relaunch,
    [string]$ScreenshotDir = 'C:\Windows\Temp\uitest-v6',
    [int]$StopTimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$processName = 'SinUpdateHub'
$deliverDir = Join-Path $root '04-交付物'
$deliver = Join-Path $deliverDir 'SinUpdateHub.exe'
$dist = Join-Path $root 'dist\SinUpdateHub.exe'

# ---------- 1. 关闭正在运行的实例（以后不需要用户自己关） ----------
$wasRunning = $false
$procs = @(Get-Process $processName -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
    $wasRunning = $true
    Write-Host "==> 关闭运行中的 SinUpdateHub（$($procs.Count) 个进程）" -ForegroundColor Cyan
    foreach ($p in $procs) {
        try { [void]$p.CloseMainWindow() } catch { }
    }

    $deadline = (Get-Date).AddSeconds($StopTimeoutSeconds)
    while ((Get-Date) -lt $deadline -and (Get-Process $processName -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 400
    }

    $left = @(Get-Process $processName -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) {
        Write-Host "    优雅关闭超时，强制结束" -ForegroundColor Yellow
        $left | Stop-Process -Force
    }

    Start-Sleep -Seconds 1
} else {
    Write-Host "==> 没有运行中的 SinUpdateHub" -ForegroundColor DarkGray
}

# ---------- 2. 发布最新 exe ----------
if ($SkipPublish) {
    if (-not (Test-Path $dist)) {
        throw "dist 里没有 exe，不能 -SkipPublish，请去掉该参数重新发布"
    }
    Write-Host "==> 跳过发布，复用 dist\SinUpdateHub.exe" -ForegroundColor DarkGray
} else {
    Write-Host "==> 发布单文件 exe" -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish.ps1') -SkipDeliverable | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "发布失败（退出码 $LASTEXITCODE）"
    }
}

# ---------- 3. 替换交付物 + 刷新源码/说明/截图（package.ps1 会复制 exe 并带重试） ----------
New-Item -ItemType Directory -Force -Path $deliverDir | Out-Null

if (Test-Path $ScreenshotDir) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'package.ps1') -ScreenshotDir $ScreenshotDir | Out-Host
} else {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'package.ps1') | Out-Host
}

# ---------- 4. 需要时重新启动 ----------
if ($Relaunch -or $wasRunning) {
    Start-Process -FilePath $deliver
    Write-Host "==> 已重新启动 SinUpdateHub" -ForegroundColor Green
}

$size = [math]::Round((Get-Item $deliver).Length / 1MB, 1)
Write-Host "==> 部署完成：$deliver（$size MB，$((Get-Item $deliver).LastWriteTime)）" -ForegroundColor Green
