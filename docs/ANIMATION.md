# VoxIME 界面动效约定

> 本文档是界面过渡/动画的模块规范。所有窗口、设置页、侧栏、最小化/恢复的显隐与尺寸变化都按这里执行。
> 实现入口：[src/VoxIME.App/Infrastructure/Motion.cs](../src/VoxIME.App/Infrastructure/Motion.cs)。AGENTS.md 只保留索引，避免把上下文撑大。

## 1. 基本原则

- **用户触发的显隐/展开/收起/最小化必须有过渡**，除非用户把「界面动效」设为关闭。
- 动效必须尊重 `Motion.Enabled` / `Motion.Style`：
  - **明显**（默认）：完整时长与位移/缩放。
  - **克制**：时长约 0.62 倍、幅度减半。
  - **关闭**：立即完成，不留动画残留状态。
- 优先动画 `Opacity` 和 `RenderTransform`（不触发布局）；侧栏这类低频容器可以动画 `Width`（见 `Motion.PlayWidth`），不要对高频内容做 Width 动画。
- 动画完成回调里必须做守卫（`IsLoaded`、`IsVisible`、`_hiding`、`_minimizing` 等），避免页面已关闭/隐藏后继续改状态。
- 禁止用 `Visibility` 瞬间切换来替代用户可感知的过渡；如果确实是瞬时状态切换，必须在注释里说明原因。

## 2. 统一时长与幅度

| 场景 | 约定 |
|---|---|
| 窗口/对话框入场 | 260–280ms，淡入 + 上浮 + 轻微放大（`Motion.PlayEnter`） |
| 窗口/对话框退场 | 160–180ms，淡出 + 下移 + 轻微缩小（`Motion.PlayExit`） |
| 设置页切换 | 200ms 水平滑入 + 淡入（`Motion.PlaySlideIn`） |
| 侧栏/预览收起展开 | 200–220ms 宽度过渡（`Motion.PlayWidth`） |
| 设置窗口最小化 | 160ms 退场后最小化；恢复 280ms 入场 |
| 动效关闭档 | 立即完成，必须复位 `Opacity`、`RenderTransform`、宽度等状态 |

## 3. 模块职责

- `Motion`：统一缓动、时长缩放、入场/退场/页面滑入/对话框/宽度过渡；新增通用动画优先加在这里，而不是在多个窗口里复制 `DoubleAnimation`。
- `SettingsWindow`：窗口入场/关闭退场/页面切换/最小化/恢复。
- `SettingsPages/*`：设置页内的可收起侧栏、预览面板等；收起时要释放昂贵资源（例如 Live2D 预览 WebView2），展开时重新初始化。
- `AppHost` / `WindowHelper`：窗口显示/隐藏/激活顺序；不要绕过 `Motion` 直接 `Show`/`Hide` 做用户可见切换。

## 4. 侧栏/预览收起展开

- 展开态：内容 + 侧栏；收起态：只保留窄栏/展开按钮。
- 收起顺序：宽度动画 → 隐藏面板 → 显示窄栏 → 释放资源。
- 展开顺序：显示面板（宽度 0）→ 宽度动画 → 初始化资源。
- 动画期间用 `_previewAnimating` 等标志阻止重复点击；完成回调必须幂等。
- 面板需 `ClipToBounds=True`，避免宽度动画期间内容溢出到左侧。
- 收起/展开状态若需要跨页面保留，应在 ViewModel/配置层持久化；当前 Live2D 预览默认每次进入页面展开。

## 5. 最小化/恢复

- 最小化按钮：先 `Motion.PlayExit`，完成后再 `WindowState=Minimized`。
- 恢复：`OnStateChanged` 检测 Minimized → 非 Minimized，`Motion.Reset` 后播放入场动画。
- 动效关闭档直接最小化；恢复路径必须复位 `RootBorder` 的 Opacity/RenderTransform，避免界面透明。
- 不要把最小化做成关闭/隐藏；托盘显隐与最小化语义不同。

## 6. 验证

- 构建后人工点一遍：设置页切换、打开/关闭设置、最小化/还原、预览收起/展开、主题切换。
- 动效关闭档必须验证一遍，确认没有透明/缩小/宽度卡住。
- UI 动效 bug 的提交信息里写清实际验证路径；必要时加临时 WPF 探针，最终不得把调试字段混入提交。
