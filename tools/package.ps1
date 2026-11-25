<#
.SYNOPSIS
    整理交付物到 04-交付物\：exe + 使用说明 + 截图 + 源码（含 zip）。
.DESCRIPTION
    交付前执行一次。要求 dist\SinUpdateHub.exe 已由 tools\publish.ps1 生成。
#>
param(
    [string]$ScreenshotDir = 'C:\Windows\Temp\uitest3'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$deliver = Join-Path $root '04-交付物'
$dist = Join-Path $root 'dist\SinUpdateHub.exe'

if (-not (Test-Path $dist)) {
    throw "没有找到发布产物：$dist（先运行 tools\publish.ps1）"
}

Write-Host "==> 清理 04-交付物（exe 若正被运行中的程序占用就跳过，后面覆盖写）"
if (Test-Path $deliver) {
    Get-ChildItem $deliver -Force | ForEach-Object {
        if ($_.Name -eq 'SinUpdateHub.exe') { return }
        try {
            Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop
        } catch {
            Write-Host "  [!] 跳过无法删除的 $($_.Name)：$($_.Exception.Message)"
        }
    }
}
New-Item -ItemType Directory -Force -Path $deliver | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $deliver '截图') | Out-Null

Write-Host "==> 复制 exe（带重试，避免杀毒/占用瞬时失败）"
$exeTarget = Join-Path $deliver 'SinUpdateHub.exe'
$copied = $false
for ($i = 1; $i -le 6; $i++) {
    try {
        Copy-Item $dist $exeTarget -Force -ErrorAction Stop
        $copied = $true
        break
    } catch {
        if ($i -eq 6) { throw "复制 exe 失败：$($_.Exception.Message)" }
        Start-Sleep -Milliseconds 800
    }
}
if (-not $copied) { throw "复制 exe 失败" }

Write-Host "==> 复制使用说明与更新日志"
Copy-Item (Join-Path $root 'docs\使用说明.md') (Join-Path $deliver '使用说明.md') -Force
$changelog = Join-Path $root 'docs\更新日志.md'
if (Test-Path $changelog) {
    Copy-Item $changelog (Join-Path $deliver '更新日志.md') -Force
}

Write-Host "==> 复制截图"
if (Test-Path $ScreenshotDir) {
    $shots = @{
        '01-main.png' = '主界面.png'
        '01c-main-checked.png' = '主界面-检查更新后.png'
        '05-editor-autodetect.png' = '添加软件-自动识别.png'
        '06-package-search.png' = '搜索WinGet包.png'
        '03b-settings-network.png' = '设置-已登录与测试网络.png'
        '02-backup-page.png' = '备份与恢复.png'
    }
    foreach ($k in $shots.Keys) {
        $src = Join-Path $ScreenshotDir $k
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $deliver "截图\$($shots[$k])") -Force
        }
    }
} else {
    Write-Host "  [!] 截图目录不存在，跳过：$ScreenshotDir"
}

Write-Host "==> 复制源码（排除 bin/obj/dist）"
$sourceOut = Join-Path $deliver '源码'
New-Item -ItemType Directory -Force -Path $sourceOut | Out-Null
$excludeDirs = @('bin', 'obj', 'dist', '.vs', '.git')
$includePaths = @('SinUpdateHub.sln', 'README.md', 'src', 'tests', 'tools', 'docs', '01-需求与方案', '05-项目日志')

foreach ($item in $includePaths) {
    $srcPath = Join-Path $root $item
    if (-not (Test-Path $srcPath)) { continue }
    if (Test-Path $srcPath -PathType Leaf) {
        Copy-Item $srcPath $sourceOut -Force
        continue
    }

    Get-ChildItem $srcPath -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($root.Length).TrimStart('\')
        $parts = $rel -split '\\'
        $skip = $false
        foreach ($p in $parts) {
            if ($excludeDirs -contains $p) { $skip = $true; break }
        }
        if ($skip) { return }

        $target = Join-Path $sourceOut $rel
        $targetDir = Split-Path $target -Parent
        New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
        Copy-Item $_.FullName $target -Force
    }
}

Write-Host "==> 打包源码 zip"
$zip = Join-Path $deliver 'SinUpdateHub-源码.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $sourceOut '*') -DestinationPath $zip -CompressionLevel Optimal

$exeSize = [math]::Round((Get-Item (Join-Path $deliver 'SinUpdateHub.exe')).Length / 1MB, 1)
$zipSize = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "==> 完成：exe $exeSize MB，源码 zip $zipSize MB" -ForegroundColor Green
Get-ChildItem $deliver | Select-Object Name, Length | Format-Table -AutoSize
