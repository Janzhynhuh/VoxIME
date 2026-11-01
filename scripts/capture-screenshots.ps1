<#
.SYNOPSIS
    启动 VoxIME 并截取桌面/设置界面截图，用于交付文档与验收。

.DESCRIPTION
    需要交互式桌面会话（本机 GUI 环境）。脚本会：
    1. 构建并启动 VoxIME.exe（可选 --settings 直接打开设置窗口）；
    2. 等待窗口出现后截取全屏；
    3. 保存到 docs/screenshots/，随后关闭应用。

.EXAMPLE
    .\scripts\capture-screenshots.ps1
    .\scripts\capture-screenshots.ps1 -Settings
#>
[CmdletBinding()]
param(
    [switch]$Settings,
    [int]$WaitSeconds = 6,
    [switch]$KeepRunning,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\VoxIME.App\bin\$Configuration\net10.0-windows\VoxIME.exe"
$shotDir = Join-Path $root 'docs\screenshots'
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

if (-not (Test-Path $exe)) {
    Write-Host "==> 可执行文件不存在，先构建：$exe" -ForegroundColor Yellow
    & dotnet build (Join-Path $root 'VoxIME.sln') -c $Configuration --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "构建失败" }
}

function Capture-Screen([string]$path) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    Write-Host "==> 已保存截图：$path" -ForegroundColor Green
}

$arguments = @()
if ($Settings) { $arguments += '--settings' }

Write-Host "==> 启动 VoxIME.exe $($arguments -join ' ')" -ForegroundColor Cyan
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $exe) -PassThru

Start-Sleep -Seconds $WaitSeconds
Capture-Screen (Join-Path $shotDir ('app-' + $(if ($Settings) { 'settings' } else { 'desktop' }) + '.png'))

if (-not $KeepRunning) {
    try {
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
            Write-Host "==> 已关闭 VoxIME（PID $($process.Id)）" -ForegroundColor Cyan
        }
    }
    catch {
        Write-Warning "关闭进程失败：$($_.Exception.Message)"
    }
}
