param(
    # 仓库根目录；默认取脚本所在目录的上一级，便于在任意位置执行
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    # 使用的 ProviderProfile Id（默认 mock；可指定已配置的真实厂商，如 mimo）
    [string]$ProviderProfileId = 'mock',
    # 指定真实语音 WAV（不传则用 440Hz 合成音验证链路）
    [string]$AudioWav = '',
    # 截图输出目录（默认 docs/screenshots；联调时可指向临时目录避免入库）
    [string]$ShotDirectory = '',
    # 强制使用 -AudioWav 指定音频（跳过真实录音，用于无声环境验证真实 ASR 接口）
    [switch]$ForceAudioWav
)

$ErrorActionPreference = 'Stop'
$Root = [System.IO.Path]::GetFullPath($Root)
$exe = Join-Path $Root 'src\VoxIME.App\bin\Debug\net10.0-windows10.0.17763.0\VoxIME.exe'
$configPath = Join-Path $env:APPDATA 'VoxIME\config.json'
$historyPath = Join-Path $env:APPDATA 'VoxIME\history.jsonl'
$logDir = Join-Path $env:APPDATA 'VoxIME\logs'
$shotDir = if ($ShotDirectory) { $ShotDirectory } else { Join-Path $Root 'docs\screenshots' }
$backup = Join-Path $env:TEMP 'voxime-config-backup.json'
$modelDir = Join-Path $Root 'assets\live2d\models\Hiyori'
$resultPath = Join-Path $env:TEMP 'voxime-e2e-result.json'
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

if (Test-Path $configPath) { Copy-Item $configPath $backup -Force }

# ---- 测试配置：Mock 服务 + 真实 Hiyori 模型 + 放宽 VAD/延长 HUD ----
# 统一用 UTF-8 读写，避免 Windows PowerShell 5.1 把无 BOM 的 UTF-8 误判为 ANSI 导致中文乱码。
$config = [System.IO.File]::ReadAllText($configPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$config.activeProviderProfileId = $ProviderProfileId
$config.audio.vadThreshold = 0.0005
$config.audio.minSpeechMs = 100
$config.audio.autoStopOnSilence = $false
$config.audio.silenceTimeoutMs = 5000
$config.audio.playCues = $false
if ($null -eq $config.live2D.modelDirectory) {
    $config.live2D | Add-Member -NotePropertyName modelDirectory -NotePropertyValue $modelDir -Force
} else {
    $config.live2D.modelDirectory = $modelDir
}
$config.live2D.opacity = 1.0
$config.live2D.clickThrough = $true
if ($null -eq $config.overlay.autoHideMs) {
    $config.overlay | Add-Member -NotePropertyName autoHideMs -NotePropertyValue 20000 -Force
} else {
    $config.overlay.autoHideMs = 20000
}
$config.overlay.showHud = $true
$config.insertion.previewBeforeInsert = $false
$config.insertion.autoInsert = $true
$config.insertion.mode = 'clipboardPaste'
$config.insertion.pasteDelayMs = 250
$config.ui.showWelcomeOnStartup = $false
[System.IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))

# ---- 清理 ----
Get-Process VoxIME -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Remove-Item $historyPath -Force -ErrorAction SilentlyContinue
Remove-Item $resultPath -Force -ErrorAction SilentlyContinue

# ---- WinForms 目标输入框（真实接收粘贴的窗口） ----
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ForegroundHelper {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    public static bool ForceForeground(IntPtr hWnd) {
        try {
            var fg = GetForegroundWindow();
            var fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
            var myThread = GetCurrentThreadId();
            if (fgThread != myThread) { AttachThreadInput(myThread, fgThread, true); }
            ShowWindow(hWnd, 9); // SW_RESTORE
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            if (fgThread != myThread) { AttachThreadInput(myThread, fgThread, false); }
            return GetForegroundWindow() == hWnd;
        } catch { return false; }
    }
}
'@
[System.Windows.Forms.Application]::EnableVisualStyles()
$form = New-Object System.Windows.Forms.Form
$form.Text = 'VoxIME E2E 目标输入框'
$form.Width = 860
$form.Height = 200
$form.StartPosition = 'CenterScreen'
$form.TopMost = $true
$form.ShowInTaskbar = $false
$textBox = New-Object System.Windows.Forms.TextBox
$textBox.Multiline = $true
$textBox.Dock = 'Fill'
$textBox.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 14)
$textBox.Text = ''
$form.Controls.Add($textBox)
$form.Show()
[void][ForegroundHelper]::ForceForeground($form.Handle)

$foregroundOk = [ForegroundHelper]::GetForegroundWindow() -eq $form.Handle
Write-Host "目标输入框已置前: $foregroundOk"
if (-not $foregroundOk) {
    Write-Warning '无法把目标输入框切到前台，为避免把文字粘贴到其他程序，本次不执行 E2E。'
    $form.Close(); $form.Dispose()
    exit 10
}

function Capture([string]$name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $path = Join-Path $shotDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "screenshot: $path"
}

function Focus-Target {
    try {
        if ([ForegroundHelper]::GetForegroundWindow() -ne $form.Handle) {
            [void][ForegroundHelper]::ForceForeground($form.Handle)
        }
        $textBox.Focus() | Out-Null
        [System.Windows.Forms.Application]::DoEvents()
    } catch { }
}

if ($AudioWav) { $env:VOXIME_E2E_AUDIO_WAV = (Resolve-Path $AudioWav).Path } else { Remove-Item Env:VOXIME_E2E_AUDIO_WAV -ErrorAction SilentlyContinue }
if ($ForceAudioWav) { $env:VOXIME_E2E_FORCE_AUDIO_WAV = '1' } else { Remove-Item Env:VOXIME_E2E_FORCE_AUDIO_WAV -ErrorAction SilentlyContinue }
$env:VOXIME_E2E_DELAY_SECONDS = '8'
$env:VOXIME_E2E_RECORD_SECONDS = '3'

$app = $null
try {
    $app = Start-Process -FilePath $exe -ArgumentList '--tray --e2e' -WorkingDirectory (Split-Path $exe) -PassThru
    Write-Host "VoxIME PID: $($app.Id)（E2E：8s 后开始 3s 录音）"

    $startedAt = Get-Date
    $shots = @{}
    while (((Get-Date) - $startedAt).TotalSeconds -lt 32) {
        [System.Windows.Forms.Application]::DoEvents()
        Focus-Target
        $elapsed = ((Get-Date) - $startedAt).TotalSeconds
        if ($elapsed -ge 4 -and -not $shots['idle']) {
            Start-Sleep -Milliseconds 200
            Capture 'app-e2e-idle.png'
            $shots['idle'] = $true
        }
        if ($elapsed -ge 11 -and -not $shots['listening']) {
            Capture 'app-e2e-listening.png'
            $shots['listening'] = $true
        }
        if ($elapsed -ge 14.5 -and -not $shots['result']) {
            Capture 'app-e2e-result.png'
            $shots['result'] = $true
        }
        if ($app.HasExited) { break }
        Start-Sleep -Milliseconds 60
    }

    # 等待应用退出（E2E 完成后 Shutdown）
    if (-not $app.HasExited) {
        $app.WaitForExit(8000) | Out-Null
    }
    $appExit = if ($app.HasExited) { $app.ExitCode } else { $null }

    [System.Windows.Forms.Application]::DoEvents()
    $targetText = $textBox.Text
    $form.Hide()

    $e2eResult = $null
    if (Test-Path $resultPath) {
        $e2eResult = [System.IO.File]::ReadAllText($resultPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    }
    $history = @()
    if (Test-Path $historyPath) { $history = Get-Content $historyPath -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json } }
    $latestLog = Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    $logTail = if ($latestLog) { Get-Content $latestLog.FullName -Tail 60 } else { @() }

    $summary = [pscustomobject]@{
        appExitCode = $appExit
        targetTextBoxText = $targetText
        e2eResult = $e2eResult
        historyCount = @($history).Count
        history = @($history)
        logTail = @($logTail)
        screenshots = @('app-e2e-idle.png', 'app-e2e-listening.png', 'app-e2e-result.png')
    }
    try {
        $summaryJson = $summary | ConvertTo-Json -Depth 12
        [System.IO.File]::WriteAllText((Join-Path $env:TEMP 'voxime-e2e-summary.json'), $summaryJson, (New-Object System.Text.UTF8Encoding($false)))
    }
    catch {
        Write-Warning "写汇总 JSON 失败（不影响验收）：$($_.Exception.Message)"
    }

    Write-Host "==== E2E 汇总 ===="
    Write-Host "app exit code: $appExit"
    Write-Host "目标文本框内容: $targetText"
    if ($e2eResult) {
        Write-Host "e2e success=$($e2eResult.success) inserted=$($e2eResult.inserted) fallback=$($e2eResult.fallbackUsed) text=$($e2eResult.text)"
    }
    Write-Host "history 条数: $(@($history).Count)"
    Write-Host "汇总文件: $env:TEMP\voxime-e2e-summary.json"
}
finally {
    if ($app -and -not $app.HasExited) { $app | Stop-Process -Force -ErrorAction SilentlyContinue }
    Get-Process VoxIME -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    try { $form.Close(); $form.Dispose() } catch { }
    if (Test-Path $backup) { Copy-Item $backup $configPath -Force }
}
