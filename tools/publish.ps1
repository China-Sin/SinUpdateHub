<#
.SYNOPSIS
    构建 SinUpdateHub（单文件可执行程序）。
.DESCRIPTION
    以 Release 配置发布为单文件、自包含（无需装 .NET 运行时）的 exe，
    输出到 dist\ 目录，并复制一份到 04-交付物\。
.EXAMPLE
    pwsh -File tools\publish.ps1
    pwsh -File tools\publish.ps1 -Runtime win-arm64
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [switch]$SkipDeliverable
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\SinUpdateHub.App\SinUpdateHub.App.csproj'
$outDir = Join-Path $root 'dist'
$deliverDir = Join-Path $root '04-交付物'

Write-Host "==> 发布 $project" -ForegroundColor Cyan
Write-Host "    配置=$Configuration  运行时=$Runtime  自包含=$(-not $FrameworkDependent)"

if (Test-Path $outDir) {
    Remove-Item $outDir -Recurse -Force
}

$args = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=none',
    '-p:SatelliteResourceLanguages=en',
    '-p:InvariantGlobalization=false',
    '-o', $outDir
)
if (-not $FrameworkDependent) {
    $args += @('--self-contained', 'true')
} else {
    $args += @('--self-contained', 'false')
}

& dotnet @args
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）"
}

$exe = Join-Path $outDir 'SinUpdateHub.exe'
if (-not (Test-Path $exe)) {
    throw "没有找到发布产物：$exe"
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "==> 完成：$exe（$size MB）" -ForegroundColor Green

if (-not $SkipDeliverable) {
    New-Item -ItemType Directory -Force -Path $deliverDir | Out-Null
    Copy-Item $exe (Join-Path $deliverDir 'SinUpdateHub.exe') -Force
    Write-Host "==> 已复制到 04-交付物\SinUpdateHub.exe" -ForegroundColor Green
}
