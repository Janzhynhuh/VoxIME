<#
.SYNOPSIS
    VoxIME Live2D 开发期截图工具：用 Edge/Chrome headless + DevTools 协议（CDP）截取
    preview 页面（支持等待真实时间让 Live2D 动画/口型生效），并可选执行一段 JS 校验参数。

.DESCRIPTION
    为什么不用 `msedge --screenshot` 简单模式：
      - 简单模式的 --virtual-time-budget 会快进虚拟时钟，requestAnimationFrame 基本不驱动，
        pixi 的 Ticker 不跑 → 模型参数（口型/眼动）不会更新，截图里 fps=0。
      - 本脚本启动 headless 浏览器 + CDP，按真实时间等待后再截图，并用 Runtime.evaluate
        读取 window.__voxime.debug() 的参数回读，可作为“渲染循环确实在跑”的证据。

    本脚本属于开发期验证工具（devtools），不参与库构建，也不影响产品运行时。

.NOTES
    依赖：Windows 自带的 System.Net.WebSockets.ClientWebSocket；Edge 或 Chrome。
    仅用于本地开发验证，不修改任何产品文件。
#>
[CmdletBinding()]
param(
    # 要打开的页面 URL（完整 URL）
    [Parameter(Mandatory)][string]$Url,
    # 截图输出路径（.png）
    [Parameter(Mandatory)][string]$OutFile,
    [int]$Width = 800,
    [int]$Height = 1000,
    # 页面加载后等待的“真实”毫秒数（让模型加载、动画、口型平滑生效）
    [int]$WarmupMs = 8000,
    # 截图前可选执行的 JS 表达式（结果打印到控制台）
    [string]$Evaluate = '',
    [string]$EdgePath = '',
    [int]$DeviceScaleFactor = 1
)

$ErrorActionPreference = 'Stop'
$outDir = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutFile))
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

if (-not $EdgePath) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe'),
        (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe')
    )
    $EdgePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $EdgePath -or -not (Test-Path $EdgePath)) { throw '找不到 Edge/Chrome 可执行文件。' }

$userDataDir = Join-Path $env:TEMP ('voxime-cdp-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
New-Item -ItemType Directory -Force -Path $userDataDir | Out-Null

$port = Get-Random -Minimum 9300 -Maximum 9899
$arguments = @(
    '--headless=new',
    '--enable-unsafe-swiftshader',
    '--disable-gpu-sandbox',
    '--hide-scrollbars',
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-extensions',
    '--mute-audio',
    "--remote-debugging-port=$port",
    "--user-data-dir=$userDataDir",
    "--window-size=$Width,$Height",
    'about:blank'
)
Write-Host "[cdp] 启动浏览器：$EdgePath (port=$port)" -ForegroundColor Cyan
$browser = Start-Process -FilePath $EdgePath -ArgumentList $arguments -PassThru

function Receive-CdpMessage {
    param([System.Net.WebSockets.ClientWebSocket]$Socket, [int]$BufferSize = 65536)
    $buffer = New-Object byte[] $BufferSize
    $segment = [ArraySegment[byte]]::new($buffer)
    $stream = New-Object System.IO.MemoryStream
    do {
        $result = $Socket.ReceiveAsync($segment, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { return $null }
        $stream.Write($buffer, 0, $result.Count)
    } while (-not $result.EndOfMessage)
    $text = [Text.Encoding]::UTF8.GetString($stream.ToArray())
    $stream.Dispose()
    return ($text | ConvertFrom-Json)
}

function Send-CdpMessage {
    param([System.Net.WebSockets.ClientWebSocket]$Socket, $Payload)
    $json = $Payload | ConvertTo-Json -Depth 20 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $segment = [ArraySegment[byte]]::new($bytes)
    $Socket.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

function Invoke-CdpCommand {
    param([System.Net.WebSockets.ClientWebSocket]$Socket, [int]$Id, [string]$Method, $Params = $null)
    $payload = @{ id = $Id; method = $Method }
    if ($null -ne $Params) { $payload['params'] = $Params }
    Send-CdpMessage -Socket $Socket -Payload $payload
    while ($true) {
        $message = Receive-CdpMessage -Socket $Socket
        if ($null -eq $message) { throw "CDP 连接已关闭（等待 $Method 响应时）" }
        if ($message.PSObject.Properties.Name -contains 'id' -and $message.id -eq $Id) {
            if ($message.PSObject.Properties.Name -contains 'error') {
                throw "CDP $Method 失败: $($message.error.message)"
            }
            return $message.result
        }
    }
}

$socket = $null
$evalText = $null
try {
    # 1) 等待 DevTools 端点就绪
    $target = $null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        try {
            $targets = Invoke-RestMethod -Uri "http://127.0.0.1:$port/json/list" -TimeoutSec 3
            $target = $targets | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
            if ($target) { break }
        } catch { Start-Sleep -Milliseconds 300 }
    }
    if (-not $target) { throw 'DevTools 端点未就绪（30s 超时）。' }

    # 2) 连接 WebSocket 并导航
    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    $socket.Options.KeepAliveInterval = [TimeSpan]::FromSeconds(20)
    $socket.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()

    [void](Invoke-CdpCommand -Socket $socket -Id 1 -Method 'Page.enable')
    [void](Invoke-CdpCommand -Socket $socket -Id 2 -Method 'Runtime.enable')
    [void](Invoke-CdpCommand -Socket $socket -Id 3 -Method 'Emulation.setDeviceMetricsOverride' -Params @{
            width = $Width; height = $Height; deviceScaleFactor = $DeviceScaleFactor; mobile = $false
        })
    [void](Invoke-CdpCommand -Socket $socket -Id 4 -Method 'Page.navigate' -Params @{ url = $Url })

    # 3) 按真实时间等待（模型加载 + 渲染循环 + 动画稳定）
    Write-Host "[cdp] 等待 $WarmupMs ms 让页面渲染稳定…" -ForegroundColor Cyan
    Start-Sleep -Milliseconds $WarmupMs

    # 4) 可选 JS 求值（用于参数回读验证）
    if ($Evaluate) {
        $evalResult = Invoke-CdpCommand -Socket $socket -Id 10 -Method 'Runtime.evaluate' -Params @{
            expression = $Evaluate; returnByValue = $true; awaitPromise = $true
        }
        if ($null -ne $evalResult.result -and $null -ne $evalResult.result.value) {
            $evalText = [string]$evalResult.result.value
        } else {
            $evalText = ($evalResult | ConvertTo-Json -Depth 10 -Compress)
        }
        Write-Host "[cdp] evaluate => $evalText" -ForegroundColor Green
    }

    # 5) 截图
    $shot = Invoke-CdpCommand -Socket $socket -Id 20 -Method 'Page.captureScreenshot' -Params @{
        format = 'png'; fromSurface = $true; captureBeyondViewport = $false
    }
    $bytes = [Convert]::FromBase64String($shot.data)
    [System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($OutFile), $bytes)
    Write-Host "[cdp] 截图已保存: $OutFile ($($bytes.Length) bytes)" -ForegroundColor Green

    # 6) 输出页面状态（便于报告/排错）
    $debug = Invoke-CdpCommand -Socket $socket -Id 21 -Method 'Runtime.evaluate' -Params @{
        expression = 'JSON.stringify(window.__voxime ? window.__voxime.debug() : {error:"no debug api"})'
        returnByValue = $true
    }
    if ($null -ne $debug.result -and $null -ne $debug.result.value) {
        Write-Host "[cdp] 页面状态: $($debug.result.value)" -ForegroundColor DarkGray
    }

    if ($evalText) { Write-Output "EVAL=$evalText" }
}
finally {
    if ($socket) {
        try { $socket.Dispose() } catch { }
    }
    if ($browser -and -not $browser.HasExited) {
        try { $browser.Kill($true) } catch { }
        Start-Sleep -Milliseconds 400
    }
}
exit 0
