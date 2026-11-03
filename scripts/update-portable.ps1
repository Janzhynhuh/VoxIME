<#
.SYNOPSIS
    更新桌面免安装版（VoxIME-Portable）。

.DESCRIPTION
    每次代码/资源改动后运行：
    - 默认重新 publish 到 deploy\VoxIME-win-x64
    - 复制到桌面 VoxIME-Portable（先关闭该目录正在运行的实例）
    - 写入 BUILD.txt（版本/提交/构建时间）
    - 重新生成桌面 VoxIME-Portable-latest.zip
    - 默认启动更新后的程序

    不修改 GitHub Release 资产；桌面目录是“最新构建”的本地副本。

.EXAMPLE
    .\scripts\update-portable.ps1
    .\scripts\update-portable.ps1 -SkipPublish -SourceDirectory .\deploy\VoxIME-win-x64
    .\scripts\update-portable.ps1 -NoLaunch
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'VoxIME-Portable'),

    [string]$SourceDirectory,

    [switch]$SkipPublish,

    [switch]$NoZip,

    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publishScript = Join-Path $PSScriptRoot 'publish-x64.ps1'
$readmeTemplate = Join-Path $PSScriptRoot 'portable-README.txt'

if (-not $SkipPublish) {
    & $publishScript
    if (-not $SourceDirectory) {
        $SourceDirectory = Join-Path $root 'deploy\VoxIME-win-x64'
    }
}

if (-not $SourceDirectory) {
    throw '使用 -SkipPublish 时必须指定 -SourceDirectory'
}

$sourceFull = [IO.Path]::GetFullPath($SourceDirectory)
$destFull = [IO.Path]::GetFullPath($Destination)
if (-not (Test-Path (Join-Path $sourceFull 'VoxIME.exe'))) {
    throw "源目录里找不到 VoxIME.exe：$sourceFull"
}

$pathRoot = [IO.Path]::GetPathRoot($destFull)
if ([string]::IsNullOrWhiteSpace($destFull) -or $destFull.TrimEnd('\') -eq $pathRoot.TrimEnd('\')) {
    throw "拒绝把根目录作为桌面免安装版目录：$destFull"
}

$destExe = Join-Path $destFull 'VoxIME.exe'
Get-Process VoxIME -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [string]::Equals($_.Path, $destExe, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        Write-Host "==> 停止正在运行的桌面免安装版：$($_.Id)" -ForegroundColor Yellow
        $_ | Stop-Process -Force
    }
Start-Sleep -Seconds 1

if (Test-Path $destFull) {
    Remove-Item -LiteralPath $destFull -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $destFull | Out-Null
Copy-Item -Path (Join-Path $sourceFull '*') -Destination $destFull -Recurse -Force

if (Test-Path $readmeTemplate) {
    Copy-Item -LiteralPath $readmeTemplate -Destination (Join-Path $destFull 'README-Portable.txt') -Force
}

$version = (Get-Item $destExe).VersionInfo.ProductVersion
$commit = 'unknown'
try {
    $commit = (git -C $root rev-parse --short HEAD 2>$null).Trim()
} catch {
    # git 不可用时仍生成构建包。
}
$buildInfo = @(
    'VoxIME 声语 - 桌面免安装版构建信息'
    "版本：$version"
    "提交：$commit"
    "构建时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    '说明：源码/文档每次改动后由 scripts/update-portable.ps1 更新；不是 GitHub Release 资产。'
) -join [Environment]::NewLine
Set-Content -LiteralPath (Join-Path $destFull 'BUILD.txt') -Value $buildInfo -Encoding utf8

$zipPath = $null
if (-not $NoZip) {
    $zipPath = Join-Path (Split-Path $destFull -Parent) 'VoxIME-Portable-latest.zip'
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Compress-Archive -Path $destFull -DestinationPath $zipPath -CompressionLevel Optimal
}

Write-Host "==> 桌面免安装版已更新：$destFull" -ForegroundColor Green
Write-Host "    版本：$version"
Write-Host "    提交：$commit"
if ($zipPath) {
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    Write-Host "    压缩包：$zipPath"
    Write-Host "    SHA256：$hash"
}

if (-not $NoLaunch) {
    Start-Process -FilePath $destExe
}
