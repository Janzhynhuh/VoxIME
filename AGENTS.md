# 给 AI/自动化协作者的仓库约定（请先读）

本仓库是 Windows 桌面语音输入法 **VoxIME 声语**。使用任何 AI 编码代理（含 Claude Code / Codex / DeepSeek Harness 等）时，请遵守以下规则。

## 1. 每次改动必须提交（强制）

- **任何**代码、文档、脚本、资源改动完成后，必须立即创建一个 git commit；不要留下未提交的工作区改动。
- 提交前必须通过：
  ```powershell
  dotnet build VoxIME.sln -c Release   # 0 警告 0 错误
  dotnet test VoxIME.sln -c Release    # 402+ 测试全绿
  ```
- commit message 使用 Conventional Commits：`feat:` / `fix:` / `docs:` / `chore:` / `test:` / `refactor:` / `perf:`，中文或英文摘要均可，正文说明"改了什么、为什么、怎么验证"。
- 一次 commit 只做一件事；不要把无关文件混进同一个 commit。

## 2. 禁止提交的内容

见 `.gitignore`：`bin/`、`obj/`、`deploy/`、`publish/`、`artifacts/`、`TestResults/`、`assets/live2d/models/`（示例模型有 Live2D 独立许可，不随仓库分发）、任何 `*.log`、本地配置、**API Key / Token / 个人隐私**。
发布包通过 GitHub Actions artifact / Release 分发，不进仓库。

## 3. 代码约定

- 技术栈：.NET 10 / C# / WPF / NAudio / System.Speech / WebView2。
- 项目分层：`VoxIME.Core`（无 UI 依赖）→ `VoxIME.Live2D` → `VoxIME.App`。
- 新增 ASR 厂商：实现 `IAsrProvider` 并在 `AsrProviderRegistry`/`AsrProviderCatalog` 注册，参考 `docs/ARCHITECTURE.md` 第 8 节。
- 公共契约（`src/VoxIME.Core/Abstractions/`、`Configuration/`、`Logging/`、`History/`）修改需同步更新 `docs/CONTRACTS.md` 和相关测试。
- 不引入未审阅的 NuGet/前端依赖；核心逻辑必须可离线单测，不要出现真实网络/麦克风依赖的测试。
- 文档：`docs/` 下的架构、产品、厂商、Live2D 文档随代码同步更新。

## 4. 已知环境注意点

- `VoxIME.App` 与 `VoxIME.Live2D` 的 TFM 必须是 `net10.0-windows10.0.17763.0`（WebView2CompositionControl 需要 WinRT 投影），Core/Tests 保持 `net10.0-windows`。
- WebView2 初始化期间**不能**把控件 `Visibility` 设为 `Collapsed`，否则 `EnsureCoreWebView2Async` 会永久挂起（见 `docs/ARCHITECTURE.md` 第 11 节）。
- 自动化 GUI 验证（`scripts/e2e-smoke.ps1`）会抢占前台输入焦点，运行前请保存工作，避免把测试文本输入到其他程序。

## 5. 发布纪律（2026-11-01 用户明确要求）

- **禁止**未经用户明确要求创建 GitHub Release、上传发布包/资产或打发布 tag。发布动作很容易消耗存储与流量，必须用户点名要求才做。
- 日常改动只需要 `git commit` + `git push` 到 `main`；CI 会自动构建并上传 7 天保留期的 workflow artifact。
- 如果确实需要给用户下载，先在回复里询问，得到确认后再执行。

## 6. 分发形态与已知问题

安装版和免安装版是**同一份 `dotnet publish` 产物**的两种分发壳，不允许复制业务代码或维护两套分支：

| 形态 | 产物 | 安装位置/使用方式 |
|---|---|---|
| 安装版 | `VoxIME-Setup-v<version>.exe`（Inno Setup） | 当前用户安装到 `%LOCALAPPDATA%\Programs\VoxIME`，无需管理员权限 |
| 免安装版 | `VoxIME-win-x64-v<version>.zip` | 解压到普通目录，双击 `VoxIME.exe` |

已知问题与约定：

- **SmartScreen**：exe/安装包未签名，首次运行可能提示「Windows 已保护你的电脑」。需要点「更多信息 → 仍要运行」。根治需要 OV/EV 代码签名证书，不能靠改代码绕过。
- **低完整性沙箱环境**：如果从带 `Low Mandatory Level` 标签的工作区/Codex 沙箱直接运行 exe，程序会变成低权限进程，表现为**无托盘图标、日志不写、配置不保存**。免安装版必须复制/解压到普通目录（桌面、D 盘、`%LOCALAPPDATA%` 等）再运行，不要直接跑仓库里的 `deploy\...\VoxIME.exe`。
- **数据目录**：两种形态默认都把配置/日志/历史放在 `%APPDATA%\VoxIME`。卸载默认保留该目录；只有显式选择「删除用户数据」才清理。免安装版目前不是「数据全在程序目录」的完全便携版。
- **API Key 明文**：从 v0.3.2 起 `config.json` 中 API Key 是明文（旧版 DPAPI 配置会自动迁移）。配置文件、导出的配置 JSON、勾选包含 Key 的备份都不能公开上传或发给别人。
- **配置迁移**：设置 → 通用 → 配置导入导出；导出时可选择是否包含 API Key。导入会替换全部设置，导入前自动备份 `config.json`，不包含历史/日志/模型。
- **WebView2 缓存**：只改 `assets/live2d/wwwroot/app.js` 时，旧版本可能继续加载缓存脚本。宿主会在启动时清理 DiskCache，并用 `app.js` 时间戳生成页面版本参数；排查「改了 JS 没生效」时先确认页面 URL 的 `v=` 是否变化。
- **托盘图标**：Windows 11 会把新图标放进「隐藏的图标」；系统设置 → 个性化 → 任务栏 → 其他系统托盘图标 里可固定。程序自身只负责创建 `NotifyIcon`，不控制 Windows 的任务栏收纳策略。
- **安装包静默测试**：在某些低权限/沙箱终端里 Inno Setup 可能因 `%TEMP%` 权限返回错误；安装包行为验证应在普通桌面会话或 CI/干净 Windows 环境执行，不要把沙箱环境报错误判为安装包缺陷。

## 7. 排查纪律（2026-11-03 用户要求）

- **先定位根因，再做生产修改**。排查期间的临时日志、调试字段、猜测性改动只能用于本地验证，**不得混入最终修复提交**。
- 修复提交的 diff 必须能逐条对应到根因、必要的防御性处理、测试和文档；无关的重构/格式化/顺手修改必须拆出去或不做。
- 提交前删除：临时 `console.log`/页面调试字段、临时配置改动、一次性脚本、临时文件、测试用的通道/端口/环境变量。
- 如果排查过程中已经提交了错误或纯调试的改动，找到根因后必须**单独提交一次还原**，不要留在代码里。
- 修 UI/渲染类 bug 时，至少保留一个可自动或半自动复核的证据（参数采样、单测、`--selftest`、CDP 采样等），并在提交信息里写清楚。

## 8. 桌面免安装版同步（强制，2026-11-03 用户要求）

- 每次完成代码/资源改动后，更新桌面免安装版：
  ```powershell
  .\scripts\update-portable.ps1 -NoLaunch
  ```
  它会重新 publish、更新 `桌面\VoxIME-Portable`、写 `BUILD.txt`、重新生成
  `桌面\VoxIME-Portable-latest.zip`。更新时用 `-NoLaunch`，由流程末尾统一启动一次。
- **标准流程（2026-11-03 用户明确要求）**：
  1. **开工前先停用便携版**：`Get-Process VoxIME` 有实例就全部 `Stop-Process`，确认进程数为 0；
  2. 改代码、构建、测试、自检（自检等一次性验证须在无实例状态下运行，验证进程自己退出）；
  3. 提交 + push；
  4. `update-portable.ps1 -NoLaunch` 更新桌面免安装版；
  5. **提交完成后自动启动一次桌面版**（`Start-Process 桌面\VoxIME-Portable\VoxIME.exe`），确认只有一个实例在运行，并在回复里给出 PID。
  - 启动前必须确认没有残留实例；**绝不允许在已有实例时再启动第二个**（会弹「VoxIME 已经在运行了」）。
  - 如果用户明确说本次不要启动，则跳过第 5 步并说明。
- 纯文档改动可跳过重建，但回复中要说明"无需更新桌面版"。
- 桌面目录是**本地最新构建**，不是 GitHub Release 资产；不要因此自动发 Release。
- 不要手工在桌面目录改文件；脚本更新时整个目录会重建。用户的配置仍在 `%APPDATA%\VoxIME`。
- 改动后回复必须给出桌面路径、构建版本/提交；如生成 zip，附 SHA256。

## 9. 提交示例

```text
feat(core): 支持流式识别会话

- 新增 IStreamingAsrSession 与 OpenAI 兼容流式实现
- 增加 12 个离线单测（假 WebSocket）
- 更新 docs/ARCHITECTURE.md 与 docs/providers.md

验证：dotnet build VoxIME.sln -c Release / dotnet test（402 通过）
```
