<#
.SYNOPSIS
    编译 SinUpdateHub 解决方案（开发时用）。
#>
param(
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

Write-Host "==> 编译解决方案（$Configuration）" -ForegroundColor Cyan
& dotnet build (Join-Path $root 'SinUpdateHub.sln') -c $Configuration -v m
if ($LASTEXITCODE -ne 0) {
    throw "编译失败（退出码 $LASTEXITCODE）"
}
Write-Host "==> 编译成功" -ForegroundColor Green
