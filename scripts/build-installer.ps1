<#
.SYNOPSIS
    构建 VoxIME Windows x64 安装包（Inno Setup）。

.DESCRIPTION
    默认先调用 publish-x64.ps1 生成自包含发布目录，再用 installer\VoxIME.iss
    编译出 deploy\VoxIME-Setup-v<version>.exe。
    CI 可先自行 dotnet publish，然后用 -SkipPublish -SourceDirectory 指定目录。

.EXAMPLE
    .\scripts\build-installer.ps1

.EXAMPLE
    .\scripts\build-installer.ps1 -SkipPublish -SourceDirectory .\artifacts\VoxIME-win-x64 -OutputDirectory .\artifacts
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version,

    [string]$SourceDirectory,

    [string]$OutputDirectory,

    [string]$InnoCompiler,

    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$issPath = Join-Path $root 'installer\VoxIME.iss'

function Get-ProjectVersion {
    $propsPath = Join-Path $root 'Directory.Build.props'
    [xml]$props = Get-Content -LiteralPath $propsPath
    $propertyGroup = $props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
    if (-not $propertyGroup -or -not $propertyGroup.Version) {
        throw "无法从 $propsPath 读取 Version"
    }

    return [string]$propertyGroup.Version
}

function Find-InnoCompiler {
    param([string]$ExplicitPath)

    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath)) {
            throw "找不到指定的 Inno Setup 编译器：$ExplicitPath"
        }

        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw '找不到 Inno Setup 6 的 ISCC.exe。请安装 Inno Setup 6，或通过 -InnoCompiler 指定路径。'
}

if (-not (Test-Path -LiteralPath $issPath)) {
    throw "找不到安装脚本：$issPath"
}

if (-not $Version) {
    $Version = Get-ProjectVersion
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "版本号格式应为 x.y.z：$Version"
}

if (-not $SkipPublish) {
    Write-Host '==> 生成自包含发布目录' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'publish-x64.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "publish-x64.ps1 失败（退出码 $LASTEXITCODE）"
    }

    if (-not $SourceDirectory) {
        $SourceDirectory = Join-Path $root 'deploy\VoxIME-win-x64'
    }
    if (-not $OutputDirectory) {
        $OutputDirectory = Join-Path $root 'deploy'
    }
}
else {
    if (-not $SourceDirectory) {
        throw '使用 -SkipPublish 时必须指定 -SourceDirectory'
    }
    if (-not $OutputDirectory) {
        $OutputDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $SourceDirectory).Path
    }
}

$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory 'VoxIME.exe'))) {
    throw "发布目录中缺少 VoxIME.exe：$SourceDirectory"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$iscc = Find-InnoCompiler -ExplicitPath $InnoCompiler

Write-Host "==> 构建安装包 VoxIME $Version" -ForegroundColor Cyan
Write-Host "    源目录：$SourceDirectory"
Write-Host "    输出目录：$OutputDirectory"
Write-Host "    ISCC：$iscc"

$isccArguments = @(
    "/DAppVersion=$Version",
    "/DSourceDirectory=$SourceDirectory",
    "/DOutputDirectory=$OutputDirectory",
    $issPath
)
& $iscc @isccArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup 编译失败（退出码 $LASTEXITCODE）"
}

$setupPath = Join-Path $OutputDirectory "VoxIME-Setup-v$Version.exe"
if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "安装包未生成：$setupPath"
}

$setup = Get-Item -LiteralPath $setupPath
$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
$sizeMb = [math]::Round($setup.Length / 1MB, 1)
Write-Host "==> 安装包完成：$setupPath（$sizeMb MB）" -ForegroundColor Green
Write-Host "    SHA256：$hash"
