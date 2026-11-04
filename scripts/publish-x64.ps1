<#
.SYNOPSIS
    发布 VoxIME Windows x64 自包含包。

.DESCRIPTION
    构建 Release 版、按 win-x64 自包含发布到 deploy\VoxIME-win-x64\，
    复制文档并生成版本信息与 zip 压缩包。

.EXAMPLE
    .\scripts\publish-x64.ps1
    .\scripts\publish-x64.ps1 -OutputDirectory D:\build\VoxIME -NoZip
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory,

    [switch]$NoZip,

    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\VoxIME.App\VoxIME.App.csproj'
$version = '0.4.2'

if (-not (Test-Path $project)) {
    throw "找不到应用项目：$project"
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root 'deploy\VoxIME-win-x64'
}

Write-Host "==> 清理输出目录：$OutputDirectory" -ForegroundColor Cyan
if (Test-Path $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
Write-Host "==> 发布 VoxIME $version（win-x64，self-contained=$selfContained）" -ForegroundColor Cyan

$publishArgs = @(
    $project,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', $selfContained,
    '-p:PublishReadyToRun=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $OutputDirectory,
    '--nologo'
)

& dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）"
}

# 复制文档与许可
$docFiles = @(
    'README.md',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md'
)
foreach ($file in $docFiles) {
    $source = Join-Path $root $file
    if (Test-Path $source) {
        Copy-Item -LiteralPath $source -Destination $OutputDirectory -Force
    }
}

$docsSource = Join-Path $root 'docs'
$docsTarget = Join-Path $OutputDirectory 'docs'
if (Test-Path $docsSource) {
    New-Item -ItemType Directory -Force -Path $docsTarget | Out-Null
    Copy-Item -Path (Join-Path $docsSource 'providers.md') -Destination $docsTarget -Force -ErrorAction SilentlyContinue
    Copy-Item -Path (Join-Path $docsSource 'live2d.md') -Destination $docsTarget -Force -ErrorAction SilentlyContinue
}

# 版本信息
$versionInfo = @"
VoxIME 声语 v$version
构建时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
目标平台：Windows x64（.NET 10，自包含）
说明：解压后双击 VoxIME.exe 启动；首次运行请在托盘图标右键 → 设置中添加语音识别服务。
"@
$versionInfo | Out-File -FilePath (Join-Path $OutputDirectory 'VERSION.txt') -Encoding utf8

$sizeMb = [math]::Round(((Get-ChildItem -Recurse -File $OutputDirectory | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Host "==> 发布完成：$OutputDirectory（$sizeMb MB）" -ForegroundColor Green

if (-not $NoZip) {
    $zipPath = Join-Path $root "deploy\VoxIME-win-x64-v$version.zip"
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Host "==> 压缩：$zipPath" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "==> 完成：$zipPath（$zipMb MB）" -ForegroundColor Green
}

Write-Host '发布产物：'
Write-Host "  $OutputDirectory"
if (-not $NoZip) {
    Write-Host "  $(Join-Path $root "deploy\VoxIME-win-x64-v$version.zip")"
}
