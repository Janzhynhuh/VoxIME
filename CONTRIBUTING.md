# 贡献指南（Contributing）

感谢关注 VoxIME 声语。本文说明本地开发、测试、提交与发布流程。

## 环境要求

- Windows 10 1809+ / Windows 11 x64
- .NET 10 SDK（`dotnet --version` ≥ 10.0）
- 可选：WebView2 Runtime（Win11 自带；Win10 需安装 Evergreen Runtime）
- 可选：用于真机联调的语音识别服务 API Key，或本地 whisper.cpp / FunASR / sherpa-onnx

## 构建与测试

```powershell
dotnet restore VoxIME.sln
dotnet build VoxIME.sln -c Release     # 必须 0 警告 0 错误
dotnet test VoxIME.sln -c Release      # 必须全绿
```

## 运行与自检

```powershell
.\scripts\run.ps1                      # 开发模式启动
.\scripts\e2e-smoke.ps1                # 端到端自检（请先保存工作：会抢占前台焦点）
.\scripts\capture-screenshots.ps1      # 启动应用并截图（可选）
```

## 提交规范（强制）

1. **每次改动都要提交**：完成一个可验证的改动后立即 commit，不要留下脏工作区。
2. 提交信息采用 Conventional Commits：`feat:`、`fix:`、`docs:`、`chore:`、`test:`、`refactor:`、`perf:`。
3. 提交前必须跑通 Release 构建与全部测试；CI 也会在 GitHub Actions 上执行同样检查。
4. 不提交构建产物、发布包、模型文件、日志、API Key（见 `.gitignore` 与 [AGENTS.md](AGENTS.md)）。

## 发布

```powershell
.\scripts\publish-x64.ps1              # 生成 deploy\VoxIME-win-x64\ 与 zip
```

标签版本（如 `v0.1.0`）由维护者打 tag，GitHub Actions 构建并上传 artifact/Release。

## 许可

- 项目代码：MIT（见 `LICENSE`）。
- 第三方组件与 Live2D 素材：见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。示例模型仅用于开发验证，请勿随仓库或发布包分发。
