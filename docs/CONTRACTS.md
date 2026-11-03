# VoxIME 开发契约（团队共享）

> 本文件是 4 名队友 + Lead 共同遵守的接口/文件边界约定。**动手前先完整读一遍**。
> 任何需要偏离本契约的改动，先 `send_message` 通知 lead，不要自行改公共文件。

## 1. 项目目标

Windows x64 语音输入法 **VoxIME 声语**：悬浮语音助手形态。

- 全局热键（按住说话 / 按下切换）录音 → 可插拔多厂商 ASR 识别 → 文本自动插入当前焦点应用。
- 透明置顶 Live2D 桌宠窗口：Cubism 3/4/5 模型、眼动跟随、待机动画、TTS 驱动口型、状态联动；无模型时用内置兜底形象。
- WPF 设置界面 + 托盘图标 + 历史记录 + 配置持久化（支持配置导入导出，API Key 明文保存）。
- 技术栈：.NET 10 / C# / WPF / NAudio / System.Speech / WebView2 / PixiJS 8 + untitled-pixi-live2d-engine。x64，Windows 11 环境已验证可构建。

## 2. 环境事实

- 工作目录：`<repo>`（所有相对路径以此为准）。
- `dotnet` 10.0.402 已安装；`dotnet build` / `dotnet test` 直接用。
- NuGet、GitHub 可访问；WebView2 Runtime 已安装（156.x）。
- **TFM 约定（2025-11-01 更新）**：`VoxIME.Core`、`VoxIME.Core.Tests` 使用 `net10.0-windows`；`VoxIME.App`、`VoxIME.Live2D` 使用 **`net10.0-windows10.0.17763.0`**（Live2D 透明合成需要 `WebView2CompositionControl` 及其 WinRT 投影 `Microsoft.Windows.SDK.NET`，最低 Windows 10 1809）。不要随意改动其他项目的 TFM。
- Python 路径：`C:\Users\<user>\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe`（用途有限）。
- 无 Visual Studio IDE，只用命令行构建；已安装 MSVC BuildTools（本项目不需要 C++）。
- 没有真实麦克风/API Key 的自动化测试环境，**ASR 网络请求一律用 HttpMessageHandler 假实现做单元测试，不要打真实网络**。

## 3. 目录与文件所有权（严禁越界写）

| 归属 | 文件范围 |
|---|---|
| **Lead** | `Directory.Build.props`、`global.json`、`VoxIME.sln`、`README.md`、`docs/`（除 `docs/live2d.md`）、`scripts/`（除 `scripts/fetch-sample-model.ps1`）、`deploy/`、`assets/app.*`、`src/VoxIME.Core/AppInfo.cs`、`src/VoxIME.Core/Abstractions/**`、`src/VoxIME.Core/Configuration/**`、`src/VoxIME.Core/Logging/**`、`src/VoxIME.Core/History/**`、`src/VoxIME.Core/Services/**`、`tests/VoxIME.Core.Tests/Integration/**`、`tests/VoxIME.Core.Tests/Configuration/**` |
| **A（core-services）** | `src/VoxIME.Core/Audio/**`、`src/VoxIME.Core/Hotkeys/**`、`src/VoxIME.Core/TextInsertion/**`、`src/VoxIME.Core/Tts/**`、`tests/VoxIME.Core.Tests/Audio/**`、`tests/VoxIME.Core.Tests/Hotkeys/**`、`tests/VoxIME.Core.Tests/TextInsertion/**`、`tests/VoxIME.Core.Tests/Tts/**` |
| **B（asr-providers）** | `src/VoxIME.Core/Asr/**`、`tests/VoxIME.Core.Tests/Asr/**`、`docs/providers.md` |
| **C（wpf-shell）** | `src/VoxIME.App/**`（可修改本目录内所有文件，不得改其他项目） |
| **D（live2d）** | `src/VoxIME.Live2D/**`、`assets/live2d/**`、`docs/live2d.md`、`scripts/fetch-sample-model.ps1` |

公共契约文件（`Abstractions/`、`Configuration/`、`Logging/`、`History/`）已冻结：**只读**。如确实需要新增接口/字段，发消息给 lead，由 lead 修改。

## 4. 已冻结的核心接口（命名空间 `VoxIME.Core.Abstractions`）

定义文件在 `src/VoxIME.Core/Abstractions/`，请直接阅读，不要重复定义：

- `AudioModels.cs`：`IAudioCaptureService`、`IBeepPlayer`、`AudioClip`、`AudioDeviceInfo`、`AudioLevelEventArgs`、`RecordingOptions`、`RecordingResult`、`AudioCue`
- `AsrModels.cs`：`IAsrProvider`、`IAsrProviderRegistry`、`AsrProviderDescriptor`、`AsrProviderPreset`、`AsrResult`、`AsrCallOptions`、`AsrException`、`AsrProviderTypeIds`（类型 ID 常量）、`AsrCategories`
- `InsertionModels.cs`：`ITextInjector`、`IClipboardService`、`ITextPostProcessor`、`InsertionResult`
- `HotkeyModels.cs`：`IHotkeyService`、`HotkeyEventArgs`、`HotkeyAction`
- `TtsModels.cs`：`ITtsService`、`TtsOptions`
- `Live2DModels.cs`：`ILive2DController`（含 `PreviewMotion`/`PreviewExpression` 预览）、`Live2DState`、`Live2DModelSizeEventArgs`（模型加载后上报原始宽高，用于窗口自适应）
- `VoiceInputModels.cs`：`IVoiceInputController`（UI 唯一依赖的主控接口）、`AppState`、`AppStateChangedEventArgs`、`TranscriptionRecord`

配置类型在 `VoxIME.Core.Configuration`：`AppConfig`（含 `ProviderProfile`、`HotkeyConfig`、`AudioConfig`、`InsertionConfig`、`Live2DConfig`、`TtsConfig`、`UiConfig`、`OverlayConfig`、`LoggingConfig`）、`IConfigStore`/`JsonConfigStore`、`ConfigExchangeService`、`ISecretProtector`/`DpapiSecretProtector`（仅旧版迁移）、`ConfigJson`。
日志：`VoxIME.Core.Logging.IAppLogger`、`FileLogger`、`NullLogger`、`LogLevel`。
历史：`VoxIME.Core.History.IHistoryStore`/`JsonlHistoryStore`。

## 5. 必须提供的具体实现类（**类名、命名空间、构造函数签名不可改**，UI/集成按此装配）

### A（core-services）
```csharp
namespace VoxIME.Core.Audio;
public sealed class WasapiAudioCaptureService : IAudioCaptureService
{
    public WasapiAudioCaptureService(IAppLogger logger);
}
public sealed class SystemBeepPlayer : IBeepPlayer
{
    public SystemBeepPlayer();
}
// VAD 需可单测（public）
public sealed class VoiceActivityDetector { /* 自行设计，须能对 16k float 采样判断语音/静音 */ }

namespace VoxIME.Core.TextInsertion;
public sealed class Win32ClipboardService : IClipboardService { public Win32ClipboardService(); }
public sealed class ClipboardTextInjector : ITextInjector
{
    public ClipboardTextInjector(IClipboardService clipboard, IAppLogger logger);
}
public sealed class DefaultTextPostProcessor : ITextPostProcessor { public DefaultTextPostProcessor(); }

namespace VoxIME.Core.Hotkeys;
public sealed class GlobalHotkeyService : IHotkeyService { public GlobalHotkeyService(IAppLogger logger); }
// 热键解析/匹配器必须 public + 可单测（例如 HotkeyGesture / HotkeyMatcher）

namespace VoxIME.Core.Tts;
public sealed class SapiTtsService : ITtsService { public SapiTtsService(IAppLogger logger); }
```

### B（asr-providers）
```csharp
namespace VoxIME.Core.Asr;
public sealed class AsrProviderRegistry : IAsrProviderRegistry
{
    public AsrProviderRegistry(HttpClient httpClient, IAppLogger logger);
}

namespace VoxIME.Core.Asr.Providers;
public sealed class OpenAiCompatibleTranscriptionProvider : IAsrProvider
{ public OpenAiCompatibleTranscriptionProvider(ProviderProfile profile, HttpClient httpClient, IAppLogger logger); }
public sealed class OpenAiAudioChatProvider : IAsrProvider { /* 同签名 */ }
public sealed class LocalCliAsrProvider : IAsrProvider { /* 同签名 */ }
public sealed class TencentAsrProvider : IAsrProvider { /* 同签名 */ }
public sealed class XfyunAsrProvider : IAsrProvider { /* 同签名 */ }
public sealed class MockAsrProvider : IAsrProvider { /* 同签名 */ }
```
`AsrProviderRegistry.Descriptors` 必须覆盖：OpenAI Whisper、硅基流动 SenseVoice、LM Studio/本地 OpenAI 兼容、**小米 MiMo ASR**、**阿里云 DashScope**、**腾讯云**、**科大讯飞**、**本地 CLI（whisper.cpp / FunASR / sherpa-onnx）**、Mock。每个 descriptor 带 1 个以上 `AsrProviderPreset`（含默认 BaseUrl/Model/Language）。

### D（live2d）
```csharp
namespace VoxIME.Live2D;
public sealed class Live2DController : ILive2DController
{
    public Live2DController(IAppLogger logger);
}
```
必须实现：透明置顶无边框窗口（不出现在任务栏）、模型目录加载（递归找 `*.model3.json`）、状态联动、口型、眼动、气泡、ClickThrough、AlwaysOnTop、Position/Size/Scale/Opacity、`ApplyConfig` 热切换、异常降级、**无模型/无 WebView2 时内置 WPF 兜底形象**；模型加载成功后通过 `ModelSizeAvailable` 上报原始宽高（`VoiceInputController` 据此把窗口宽度收紧到模型显示宽度 + 5 DIP padding，`Live2DConfig.AutoFitWindow` 可关闭）。

### Lead
```csharp
namespace VoxIME.Core.Services;
public sealed class VoiceInputController : IVoiceInputController
{
    public VoiceInputController(IConfigStore configStore, IAudioCaptureService audio,
        IAsrProviderRegistry asrRegistry, ITextInjector injector, ITextPostProcessor postProcessor,
        ITtsService tts, IHotkeyService hotkeys, ILive2DController live2d, IBeepPlayer beep,
        IHistoryStore history, IAppLogger logger);
}
```

## 6. 构建与测试命令

```powershell
# 只构建自己负责的项目（推荐，避免被他人未完成的代码阻塞）
dotnet build src/VoxIME.Core/VoxIME.Core.csproj
dotnet build src/VoxIME.Live2D/VoxIME.Live2D.csproj
dotnet build src/VoxIME.App/VoxIME.App.csproj

# 单元测试（第一遍构建会还原，约 30s）
dotnet test tests/VoxIME.Core.Tests/VoxIME.Core.Tests.csproj --nologo

# 全解决方案（Lead 最终集成用）
dotnet build VoxIME.sln
```
注意：`VoxIME.App` 在 A/B/D 的实现类出现前无法编译通过（AppHost 引用它们）。C 可以先写完 UI/ViewModel，构建失败时确认报错只涉及尚未出现的实现类即可。

## 7. 通用编码要求

- 语言：代码用 C# 12+，标识符英文；**UI 文案、错误提示、注释、文档用简体中文**。
- 空值处理：项目开启 `Nullable`，不要用 `!` 乱压；对不可信输入做防御。
- 线程：Core 服务不要假设 UI 线程；事件在后台线程触发时由 UI 层自行 Dispatcher。避免 `async void`（事件处理器除外）。
- 异常：不要让后台线程未捕获异常导致崩溃；对外错误通过事件/`AsrException` 汇报并记日志。
- 不引入新的 NuGet 包（现有：NAudio、System.Speech、ProtectedData、WebView2、xunit）。需要新的先问 Lead。
- 不要修改 `Directory.Build.props`、`global.json`、`VoxIME.sln`、其他项目的 `.csproj`；不要 `git init`/`git commit`；不要 `dotnet publish`。
- 不写死绝对路径；用户数据目录用 `AppInfo.AppDataDirectory`。
- 不要删除/覆盖其他队友正在写的文件。
- WPF 资源（样式/图片）自己负责；D 的 `assets/live2d/wwwroot/**` 由 Live2D.csproj 以 Content 复制到输出目录，网页用相对路径引用。

## 8. 工作流

1. `team_task_get` 读取自己的任务 → `team_task_update`（action=claim, expected_revision=当前 revision）认领。
2. 按任务描述 + 本契约实现，边写边用 `dotnet build` 自检。
3. 完成后：`dotnet build`/`dotnet test` 通过 → `team_task_update`（action=complete）→ `send_message` 给 lead，内容：完成内容、关键文件列表、构建/测试结果、未完成/风险点。
4. 遇到契约冲突、缺依赖、需要拆分的决策：**立即**发消息给 lead，不要卡住超过 10 分钟。
5. Lead 会做最终集成与发布；队友不需要打包、不需要写 README。

## 9. 交互/配色

- 主题与配色自 2026-11-03 起分离为两个维度（见第 10 节）；界面颜色不允许再写死。
- 默认外观：深色 + VoxIME 紫罗兰（主背景 `#12121A`、卡片 `#1C1C27`、强调 `#7C5CFF`、正文 `#E8E8F0`、次要文字 `#8A8AA0`、成功 `#4ADE80`、危险 `#FF6B6B`、警告 `#FBBF24`）。
- 字体：`Microsoft YaHei UI`，圆角 12px，无边框窗口可拖动。
- 中文界面：状态文案："待机 / 聆听中… / 识别中… / 朗读中 / 出错了"。
- 设置窗口约 980×700，左侧导航（服务、录音、上屏、桌宠、朗读、通用、外观），右侧内容区。
- HUD：屏幕底部居中，胶囊卡片，含麦克风电平环、状态文字、识别预览、插入/复制/取消按钮；点击按钮区域不穿透。

## 10. 主题与配色契约（2026-11-03 新增）

配置文件（`UiConfig`）：

- `theme`：`system`（跟随 Windows 应用模式）/ `light` / `dark`；`AppConfig.Normalize` 负责规范化，非法值回退 `dark`。
- `colorScheme`：`voxime` / `catppuccin-latte` / `catppuccin-frappe` / `catppuccin-macchiato` / `catppuccin-mocha`；非法值回退 `voxime`。
- 旧字段 `accentColor` 已移除（此前只是占位、从未接线）。

Core（`VoxIME.Core.Appearance`，无 UI 依赖，可离线单测）：

- `ThemeModes`：规范化主题模式、按「模式 + 系统明暗」判断实际明暗。
- `ThemeCatalog`：配色方案目录。`voxime` 同时提供浅/深两套色；Catppuccin Latte 仅浅色，Frappé / Macchiato / Mocha 仅深色。
- `ThemeResolver.Resolve(theme, colorScheme, systemIsLight)` → `ResolvedTheme`；模式失配时同族回退（Catppuccin → Latte/Mocha，其它 → `voxime`）。
- `ThemeTokens.Build(ThemeColorSet, isLight)` → 完整颜色资源键：普通颜色键以 `Color` 结尾（UI 层自动生成同名 `*Brush`），渐变端点用 `AccentGradient*` / `HeroGradient*`。

App（`VoxIME.App.Infrastructure`）：

- `ThemeManager`：启动/保存时把令牌展开为 `ResourceDictionary` 注入 `Application.Resources`；对外 `Current`（`ResolvedTheme`）与 `ThemeChanged`；`IsSystemLight` 读注册表 `AppsUseLightTheme`，订阅 `SystemEvents.UserPreferenceChanged` 实现跟随系统。
- XAML 硬性规则：颜色只能写 `{DynamicResource ...}`；**禁止**硬编码十六进制颜色，禁止把颜色资源用 `StaticResource` 烘焙进样式（否则实时换肤失效、浅色主题错色）。新增颜色先加 `ThemeTokens` 派生键，再在 `ThemeCatalog` 各配色里给语义值。
- 主题无关资源（字体、圆角、黑色阴影、`WhiteBrush`）放 `Themes/Base.xaml`；调色板不落 XAML，由 `ThemeManager` 生成。

### 10.1 背景与动效契约（2026-11-03 第二批）

配置文件（`UiConfig`）：

- `motion`：`expressive`（明显，默认）/ `subtle`（克制）/ `off`（关闭）；`Normalize` 非法值回退 `expressive`。
- `background`：只作用于设置/历史/关于/欢迎等低频窗口，**HUD 与 Live2D 桌宠不使用**。字段：
  - `mode`：`theme`（默认）/ `solid` / `gradient` / `image`；
  - `imagePath`：选择图片时复制到 `%APPDATA%\VoxIME\backgrounds`（源文件移动/删除不影响）；
  - `imageOpacity` 0.2–1.0、`blurRadius` 0–60、`dim` 0–0.95、`fit`（`cover`/`contain`/`fill`）、`surfaceOpacity` 0.35–1.0（玻璃强度）；
  - `solidColor` / `gradientStart` / `gradientEnd`：`#RRGGBB`，空=跟随主题。文字颜色由主题的浅色/深色决定（浅色主题=深色字、深色主题=浅色字）；外观页提供「按背景亮度匹配主题」：按自定义背景的平均亮度自动切浅色/深色主题，不再单独做文字反色。

Core：

- `VoxIME.Core.Appearance.MotionModes`：动效档位规范化/解析。
- `VoxIME.Core.Configuration.BackgroundConfig`：背景字段、`IsCustom`、`Normalize()`；颜色非法时置空（跟随主题）。

App：

- `Infrastructure.Motion`：动效 token（明显档时长/位移，克制档按时长 ×0.62、幅度 ×0.5），提供 `PlayEnter/PlayExit/PlaySlideIn/PlayDialogEnter/PlayLift/PlayPress`；只动画 `Opacity` 与 `RenderTransform`，XAML 不写 Storyboard 时长。
- `Infrastructure.BackgroundRenderer`：把背景配置展开为 `WindowBackgroundBrush` / `WindowDimBrush` 两个 DynamicResource；图片先按 980 宽解码 + `RenderTargetBitmap` 预烘焙模糊并缓存，不挂实时 `BlurEffect`；图片模式会让 `Card/CardHover/Input/Popup/Nav*/Bg/AccentSoft` 画刷按 `surfaceOpacity` 变半透明（玻璃质感）。消息框使用不受玻璃影响的 `DialogCardBrush`。
- `Infrastructure.BackgroundImageStore`：背景图片本机存储（复制/清理旧图，只保留最近 8 张）。
- 窗口 XAML 约定：低频窗口根 `Border.Background` 用 `{DynamicResource WindowBackgroundBrush}`，内部第一层加 `{DynamicResource WindowDimBrush}` 遮罩（`CornerRadius` 必须与窗口一致，主题色模式下为透明）；HUD/桌宠保持原样。`ScrollViewer` 用 `inf:SmoothScroll.IsEnabled="True"` 启用滚轮平滑惯性：平滑滚动与拖动滚动条同路径：每帧调用 ScrollToVerticalOffset 把真实偏移推向目标，滚动条/内容天然同步。运动模型为指数跟随目标（`VoxIME.Core.Services.ScrollMotion`，τ≈50ms（优先用合成器 RenderingTime，回退 Stopwatch 高精度帧间隔））：滚轮累加目标、偏移连续逼近，无逐格脉冲；程序化滚动入口为 `SmoothScroll.ScrollBy`。滚动不再触碰任何窗口特效；只处理纵向滚轮，文本输入框与拖拽滚动条保持原生。
- 窗口阴影：设置/历史/关于/欢迎用 `inf:WindowShadowLayer`（静态阴影位图，首次显示生成并缓存，禁止在根容器上挂 `DropShadowEffect`）；消息框等小窗口可继续用 `WindowShadow` 资源。
