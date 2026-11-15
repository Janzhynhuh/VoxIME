#requires -Version 7.0
<#
.SYNOPSIS
    构建 VoxIME.NativePet（原生桌宠进程）。
.DESCRIPTION
    默认构建窗口/D3D/IPC 骨架；传入 CubismSdk 或设置 VOXIME_CUBISM_SDK 后，
    会链接本地 Cubism Native SDK 并复制 Core DLL/FrameworkShaders。
    默认构建到 artifacts\native-pet（已在 .gitignore 中）。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$BuildDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\native-pet'),

    [string]$Generator = 'Visual Studio 18 2026',

    [string]$CubismSdk = $env:VOXIME_CUBISM_SDK
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceDir = Join-Path $repoRoot 'src\VoxIME.NativePet'

function Find-CMake {
    $candidates = @()

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $install = & $vswhere -latest -products * -property installationPath 2>$null
        if ($install) {
            $candidates += Join-Path $install 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
        }
    }

    # 本机常见路径兜底（VS2026/VS18 Community）。
    $candidates += 'D:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'

    $command = Get-Command cmake -ErrorAction SilentlyContinue
    if ($command) {
        $candidates += $command.Source
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    throw '找不到 cmake.exe。请安装 Visual Studio 的“使用 C++ 的桌面开发”工作负载，或把 cmake 加入 PATH。'
}

$cmake = Find-CMake
Write-Host "==> CMake: $cmake"

$cmakeArguments = @("-DCMAKE_CONFIGURATION_TYPES=$Configuration")
if ($CubismSdk) {
    if (-not (Test-Path (Join-Path $CubismSdk 'Core')) -or -not (Test-Path (Join-Path $CubismSdk 'Framework'))) {
        throw "CubismSdk 路径无效（需要 Core/ 和 Framework/）：$CubismSdk"
    }
    Write-Host "==> Cubism SDK: $CubismSdk"
    $cmakeArguments += "-DCUBISM_SDK_DIR=$CubismSdk"
}
else {
    Write-Host '==> 未设置 CubismSdk，只构建窗口/D3D/IPC 骨架'
}

Write-Host "==> 配置 NativePet（$Configuration，$Generator）"
& $cmake -S $sourceDir -B $BuildDir -G $Generator -A x64 @cmakeArguments
if ($LASTEXITCODE -ne 0) {
    throw "CMake 配置失败（exit $LASTEXITCODE）"
}

Write-Host '==> 编译 NativePet'
& $cmake --build $BuildDir --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) {
    throw "NativePet 编译失败（exit $LASTEXITCODE）"
}

$exe = Join-Path $BuildDir "$Configuration\VoxIME.NativePet.exe"
if (-not (Test-Path $exe)) {
    throw "未找到构建产物：$exe"
}

Write-Host "==> 完成：$exe"
