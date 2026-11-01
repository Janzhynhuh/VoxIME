# 第三方组件与素材许可说明

VoxIME 代码以 MIT 许可发布（见根目录 `LICENSE`）。以下是运行时依赖的第三方组件与素材，使用/分发前请遵守各自的许可条款。

## NuGet 依赖

| 组件 | 许可证 | 用途 |
|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) 2.2.1 | MIT | WASAPI 采集、重采样、WAV 播放、提示音 |
| [System.Speech](https://www.nuget.org/packages/System.Speech)（.NET 兼容包） | MIT | Windows SAPI 语音合成 |
| [System.Security.Cryptography.ProtectedData](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData) | MIT | DPAPI 加密 API Key |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Microsoft 许可（可再分发） | 承载 Live2D 网页 |
| [Microsoft.Windows.SDK.NET.Ref](https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref)（经 TFM `net10.0-windows10.0.17763.0` 隐式引入） | Microsoft 许可 | WebView2CompositionControl 所需 WinRT 投影 |
| [xunit](https://xunit.net/) 及测试依赖 | Apache-2.0 | 单元测试（仅开发） |

## Live2D 相关

| 组件 | 许可 | 说明 |
|---|---|---|
| [Live2D Cubism Core](https://www.live2d.com/sdk/web/)（`live2dcubismcore.min.js`） | Live2D 专有许可 | 随网页运行时加载；**商业使用/再分发需遵守 Live2D Cubism SDK Release License**（个人/小规模通常免费，企业需授权） |
| [PixiJS](https://pixijs.com/) 8.22.0 | MIT | Live2D 渲染基础（WebGL） |
| [untitled-pixi-live2d-engine](https://www.npmjs.com/package/untitled-pixi-live2d-engine) 1.4.0 | MIT | PixiJS v8 的 Cubism 2–5 渲染引擎（本仓库使用 Cubism 3/4/5 bundle；许可原文见 `assets/live2d/wwwroot/vendor/untitled-pixi-live2d-engine.LICENSE`） |
| Live2D 官方示例模型（Hiyori 等） | Live2D 示例模型使用条款 | **仅用于开发/测试演示**，不随发布包分发；用户需自行从官方获取并遵守条款 |

> 重要：Live2D 的模型数据（.moc3/.model3.json/贴图）版权归原作者所有。VoxIME 只提供加载能力，不提供任何模型下载；示例模型脚本仅用于本地开发验证，请勿将示例模型用于商业用途。

## 其他

| 资源 | 许可 | 说明 |
|---|---|---|
| .NET 10 Runtime | MIT | 自包含发布包中已包含 |
| 应用图标 `assets/app.ico` | 本项目原创 | 可随项目分发 |
| 字体 | 系统字体（Microsoft YaHei UI） | 不随包分发 |

## 各语音识别服务

VoxIME 本身不包含任何语音识别模型或服务，仅通过 HTTPS/WebSocket/本地进程调用用户自行配置的服务。各厂商 API 的计费、数据使用政策与合规要求请以厂商官方文档和协议为准（OpenAI、硅基流动、小米 MiMo 开放平台、阿里云百炼/DashScope、腾讯云、科大讯飞等）。

## 免责声明

本项目为个人学习/效率工具，与上述任何厂商无隶属或合作关系。使用者需自行承担 API 费用、数据合规与模型许可责任。
