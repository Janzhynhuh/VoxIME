<#
.SYNOPSIS
    下载 Live2D 官方示例模型（默认 Hiyori / 恋爱小日子系列）到本地，供 VoxIME 桌宠开发期验证渲染。

.DESCRIPTION
    从 GitHub 仓库 Live2D/CubismWebSamples 下载指定模型目录的全部文件（.model3.json / .moc3 /
    textures / motions / physics3 / pose3 / cdi3 / userdata3），保存到 assets/live2d/models/<模型名>。
    下载源优先 raw.githubusercontent.com，失败时回退 GitHub Git Blob API（部分网络环境 raw 域名
    解析异常时仍可成功）。不使用 git、不依赖 npm。

    注意：assets/live2d/models/ 已被 .gitignore 忽略，示例模型仅用于本地开发验证，不随源码分发。

.PARAMETER Destination
    目标目录。默认为 <仓库根>/assets/live2d/models/<ModelName>。

.PARAMETER ModelName
    CubismWebSamples 中的示例模型目录名，默认 Hiyori。

.PARAMETER Repo
    GitHub 仓库，默认 Live2D/CubismWebSamples。

.PARAMETER Ref
    分支/提交，默认 develop。

.PARAMETER Force
    目标已存在时也重新下载覆盖。

.EXAMPLE
    pwsh -File scripts/fetch-sample-model.ps1
    pwsh -File scripts/fetch-sample-model.ps1 -ModelName Hiyori -Force

.NOTES
    ============ 许可与使用条款（重要） ============
    1) Live2D Cubism Core（live2dcubismcore.min.js / Cubism Core 原生库）属于 Live2D 专有软件，
       遵循“Live2D Proprietary Software License Agreement”。随本仓库分发的 Cubism Core 仅可按
       Live2D Cubism SDK 的条款用于开发/发布与 Cubism 兼容的应用；发布应用需要遵守 Live2D 的
       发布许可（年收入超过阈值的小规模事业者/一般事业者需分别签订相应许可）。
    2) CubismWebSamples 中的示例模型（如 Hiyori）是 Live2D Inc. 提供的“示例模型数据”，
       仅允许按随附条款用于测试/开发与展示；不得单独再分发模型数据，商业使用需获得 Live2D 授权。
    3) 用户自带模型（Cubism 3/4 的 .model3.json + .moc3 + 贴图 + 动作完整目录）的前提是用户拥有
       该模型的合法使用权。VoxIME 本身不附带任何商业模型。
    详见 docs/live2d.md“许可与合规”章节。
#>
[CmdletBinding()]
param(
    [string]$Destination,
    [string]$ModelName = 'Hiyori',
    [string]$Repo = 'Live2D/CubismWebSamples',
    [string]$Ref = 'develop',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Destination) {
    $Destination = Join-Path (Join-Path $repoRoot 'assets\live2d\models') $ModelName
}
$Destination = [System.IO.Path]::GetFullPath($Destination)
$modelPath = "Samples/Resources/$ModelName"

Write-Host ''
Write-Host '======================================================================' -ForegroundColor Magenta
Write-Host ' VoxIME 示例模型下载器' -ForegroundColor Magenta
Write-Host '======================================================================' -ForegroundColor Magenta
Write-Host " 仓库    : $Repo ($Ref)"
Write-Host " 模型    : $ModelName"
Write-Host " 目标目录: $Destination"
Write-Host ''
Write-Host ' 许可提示：示例模型（Hiyori 等）与 Cubism Core 均为 Live2D Inc. 版权作品，' -ForegroundColor Yellow
Write-Host ' 仅可用于开发/测试；商业使用需遵守 Live2D Cubism SDK 发布许可，且不得单独再分发模型数据。' -ForegroundColor Yellow
Write-Host ' 详见 docs/live2d.md「许可与合规」。' -ForegroundColor Yellow
Write-Host ''

$modelJson = Join-Path $Destination "$ModelName.model3.json"
if ((Test-Path $modelJson) -and -not $Force) {
    Write-Host "[跳过] 已存在 $modelJson" -ForegroundColor Green
    Write-Host '       如需重新下载请加 -Force。'
    exit 0
}

# ---------------------------------------------------------------------------
# 1) 获取文件列表：优先 GitHub API tree；API 不可用时退回内置 Hiyori 清单。
# ---------------------------------------------------------------------------
$apiHeaders = @{
    'User-Agent'    = 'VoxIME-Live2D-Fetch/1.0'
    'Accept'        = 'application/vnd.github+json'
}

function Get-FileManifest {
    param([string]$Repo, [string]$Ref, [string]$ModelPath, [string]$ModelName)

    $treeUrl = "https://api.github.com/repos/$Repo/git/trees/$Ref" + '?recursive=1'
    try {
        $tree = Invoke-RestMethod -Uri $treeUrl -Headers $apiHeaders -TimeoutSec 60
        $entries = @($tree.tree | Where-Object {
                $_.type -eq 'blob' -and $_.path -like "$ModelPath/*"
            })
        if ($entries.Count -gt 0) {
            Write-Host "[列表] GitHub API 返回 $($entries.Count) 个文件" -ForegroundColor Cyan
            return @($entries | ForEach-Object {
                    [pscustomobject]@{
                        Relative = $_.path.Substring($ModelPath.Length + 1)
                        Sha      = $_.sha
                        Size     = [long]$_.size
                    }
                })
        }
        Write-Warning "API 未返回 $ModelPath 下的文件，尝试内置清单。"
    }
    catch {
        Write-Warning "GitHub API 不可用（$($_.Exception.Message)），尝试内置清单。"
    }

    if ($ModelName -ne 'Hiyori') {
        throw "无法获取模型 $ModelName 的文件列表，且没有内置清单。请检查网络或手动下载模型目录到 $Destination。"
    }

    $fallback = @(
        'Hiyori.2048/texture_00.png',
        'Hiyori.2048/texture_01.png',
        'Hiyori.cdi3.json',
        'Hiyori.moc3',
        'Hiyori.model3.json',
        'Hiyori.physics3.json',
        'Hiyori.pose3.json',
        'Hiyori.userdata3.json'
    )
    for ($i = 1; $i -le 10; $i++) {
        $fallback += ('motions/Hiyori_m{0:D2}.motion3.json' -f $i)
    }
    Write-Host "[列表] 使用内置 Hiyori 清单（$($fallback.Count) 个文件）" -ForegroundColor Cyan
    return @($fallback | ForEach-Object {
            [pscustomobject]@{ Relative = $_; Sha = $null; Size = 0L }
        })
}

$manifest = Get-FileManifest -Repo $Repo -Ref $Ref -ModelPath $modelPath -ModelName $ModelName

# ---------------------------------------------------------------------------
# 2) 逐文件下载（raw 优先，Blob API 回退，每个文件重试 3 次）。
# ---------------------------------------------------------------------------
function Save-RemoteFile {
    param(
        [string]$Repo,
        [string]$Ref,
        [string]$RemotePath,
        [string]$Sha,
        [string]$Target
    )

    $rawUrl = "https://raw.githubusercontent.com/$Repo/$Ref/$RemotePath"
    $blobUrl = if ($Sha) { "https://api.github.com/repos/$Repo/git/blobs/$Sha" } else { $null }
    $attempts = @()
    $attempts += , @{ Kind = 'raw'; Url = $rawUrl }
    if ($blobUrl) { $attempts += , @{ Kind = 'blob'; Url = $blobUrl } }

    foreach ($attempt in $attempts) {
        for ($retry = 1; $retry -le 2; $retry++) {
            try {
                if ($attempt.Kind -eq 'blob') {
                    $resp = Invoke-RestMethod -Uri $attempt.Url -Headers $apiHeaders -TimeoutSec 120
                    $bytes = [Convert]::FromBase64String($resp.content)
                    [System.IO.File]::WriteAllBytes($Target, $bytes)
                }
                else {
                    Invoke-WebRequest -Uri $attempt.Url -OutFile $Target -UseBasicParsing -TimeoutSec 180
                }
                return
            }
            catch {
                if ($retry -eq 2) {
                    Write-Verbose "下载失败 [$($attempt.Kind)] $RemotePath : $($_.Exception.Message)"
                }
                else {
                    Start-Sleep -Milliseconds (300 * $retry)
                }
            }
        }
    }

    throw "下载失败：$RemotePath（raw 与 Blob API 均不可用）。请检查网络/代理后重试，或手动下载。"
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$done = 0
$total = $manifest.Count
$totalBytes = 0L
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($item in $manifest) {
    $relative = $item.Relative.Replace('/', [string][System.IO.Path]::DirectorySeparatorChar)
    $target = Join-Path $Destination $relative
    $parent = Split-Path $target -Parent
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $remotePath = "$modelPath/$($item.Relative)"
    Save-RemoteFile -Repo $Repo -Ref $Ref -RemotePath $remotePath -Sha $item.Sha -Target $target

    $size = (Get-Item $target).Length
    $totalBytes += $size
    $done++
    Write-Host ("  [{0,2}/{1}] {2,10:N0} B  {3}" -f $done, $total, $size, $item.Relative)
}

$stopwatch.Stop()

# ---------------------------------------------------------------------------
# 3) 校验关键文件与 JSON。
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '[校验] 检查模型完整性...' -ForegroundColor Cyan
if (-not (Test-Path $modelJson)) {
    throw "缺少 $ModelName.model3.json，下载不完整。"
}

$json = Get-Content $modelJson -Raw | ConvertFrom-Json
$fileRefs = $json.FileReferences
$missing = @()
if (-not $fileRefs.Moc) { $missing += 'FileReferences.Moc 未声明' }
elseif (-not (Test-Path (Join-Path $Destination $fileRefs.Moc))) { $missing += "缺少 Moc 文件: $($fileRefs.Moc)" }

$textureCount = 0
foreach ($tex in @($fileRefs.Textures)) {
    $textureCount++
    if (-not (Test-Path (Join-Path $Destination $tex))) { $missing += "缺少贴图: $tex" }
}
if ($textureCount -eq 0) { $missing += 'FileReferences.Textures 为空' }

$motionGroups = @()
if ($fileRefs.Motions) {
    $motionGroups = @($fileRefs.Motions.PSObject.Properties.Name)
    $motionCount = 0
    foreach ($group in $motionGroups) {
        foreach ($motion in @($fileRefs.Motions.$group)) {
            $motionCount++
            $motionFile = Join-Path $Destination $motion.File
            if (-not (Test-Path $motionFile)) { $missing += "缺少动作文件: $($motion.File)" }
        }
    }
    Write-Host "  动作组: $($motionGroups -join ', ')（共 $motionCount 个 motion）"
}
Write-Host "  贴图: $textureCount 张；Moc: $($fileRefs.Moc)"
Write-Host "  文件数: $total；总大小: $([math]::Round($totalBytes / 1MB, 2)) MB；耗时 $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1))s"

if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host '以下文件缺失:' -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    throw '模型下载不完整，请加 -Force 重试或手动下载。'
}

Write-Host ''
Write-Host '下载完成，模型可用。' -ForegroundColor Green
Write-Host ''
Write-Host '下一步（本地预览，无需构建）：' -ForegroundColor Gray
Write-Host "  1. cd `"$(Join-Path $repoRoot 'assets\live2d')`""
Write-Host '  2. python -m http.server 8765     # 或任意静态文件服务器'
Write-Host "  3. 浏览器打开 http://127.0.0.1:8765/wwwroot/preview.html?model=../models/$ModelName/$ModelName.model3.json"
Write-Host '  4. Edge headless 截图:'
Write-Host "     msedge --headless=new --enable-unsafe-swiftshader --window-size=800,1000 --screenshot=docs/screenshots/live2d-preview.png `"http://127.0.0.1:8765/wwwroot/preview.html?model=../models/$ModelName/$ModelName.model3.json`""
Write-Host ''
Write-Host '在 VoxIME 中使用：设置 → 桌宠 → 选择模型目录 → 选择' -ForegroundColor Gray
Write-Host "  $Destination" -ForegroundColor Gray
exit 0
