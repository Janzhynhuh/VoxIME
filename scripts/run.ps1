<#
.SYNOPSIS
    开发模式构建并运行 VoxIME（不打包）。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $NoBuild) {
    Write-Host "==> 构建 VoxIME.sln（$Configuration）" -ForegroundColor Cyan
    & dotnet build (Join-Path $root 'VoxIME.sln') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "构建失败（退出码 $LASTEXITCODE）"
    }
}

$exe = Join-Path $root "src\VoxIME.App\bin\$Configuration\net10.0-windows\VoxIME.exe"
if (-not (Test-Path $exe)) {
    throw "找不到可执行文件：$exe（请先构建）"
}

Write-Host "==> 启动 $exe" -ForegroundColor Green
Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
