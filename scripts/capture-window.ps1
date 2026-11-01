<#
.SYNOPSIS
    通过 PrintWindow 抓取 VoxIME 指定窗口（即使被其他窗口遮挡）。

.EXAMPLE
    .\scripts\capture-window.ps1 -Output docs\screenshots\app-settings.png
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Arguments = '--settings',
    [string]$TitlePattern = '设置',
    [string]$Output = 'docs\screenshots\app-settings.png',
    [int]$WaitSeconds = 12
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
    $Exe = Join-Path $root 'src\VoxIME.App\bin\Debug\net10.0-windows10.0.17763.0\VoxIME.exe'
}

$OutputPath = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class WindowCaptureNative {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static List<IntPtr> FindWindows(uint pid, string pattern) {
        var result = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (IsWindowVisible(h) && title.Length > 0 && (pattern.Length == 0 || title.Contains(pattern))) result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

Get-Process VoxIME -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

$process = Start-Process -FilePath $Exe -ArgumentList $Arguments -WorkingDirectory (Split-Path -Parent $Exe) -PassThru
Write-Host "VoxIME PID: $($process.Id)，等待窗口（$TitlePattern）…"

$hwnd = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    $windows = [WindowCaptureNative]::FindWindows([uint32]$process.Id, $TitlePattern)
    if ($windows.Count -gt 0) { $hwnd = $windows[0]; break }
}

if ($hwnd -eq [IntPtr]::Zero) {
    Get-Process VoxIME -ErrorAction SilentlyContinue | Stop-Process -Force
    throw "未找到窗口（$TitlePattern）"
}

[void][WindowCaptureNative]::ShowWindow($hwnd, 9)
[void][WindowCaptureNative]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 1200

$rect = New-Object WindowCaptureNative+RECT
[void][WindowCaptureNative]::GetWindowRect($hwnd, [ref]$rect)
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
Write-Host "窗口句柄=$hwnd 尺寸=${width}x${height}"

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
$ok = [WindowCaptureNative]::PrintWindow($hwnd, $hdc, 2) # PW_RENDERFULLCONTENT
$graphics.ReleaseHdc($hdc)
$graphics.Dispose()
Write-Host "PrintWindow 结果：$ok"

if (-not $ok) {
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $width, $height))
    $graphics.Dispose()
}

$bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Host "截图已保存：$OutputPath"

Get-Process VoxIME -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
