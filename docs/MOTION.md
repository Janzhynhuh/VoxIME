# VoxIME 动效规范（Motion Spec）

> 状态：v1.0（2026-11-04，已实现）
> 定位：设计系统（Design System）中的「动效规范」，不是某一个按钮的定制实现。
> 目标：先定义「哪类控件、哪个状态、动什么属性、用哪条曲线、多快」，再由统一的执行层实现；新增按钮/列表/开关只需套用契约，不写单独动画代码。

## 1. 目的与范围

- 统一设置窗口、历史窗口、HUD、欢迎引导等界面的交互动效；
- 覆盖：按钮（主/次/文字/图标）、开关、导航项、列表项；
- 不覆盖：页面切换、窗口入场、主按钮以外的图标光晕等已有独立动效（由 `Motion` 现有方法负责）。

## 2. 动效 token

所有控件共用一套 token，禁止在模板里硬编码时长或曲线。

| Token | 值 | 用途 |
|---|---|---|
| `Fast` | 90ms | 按下、快速反馈 |
| `Base` | 150ms | hover、普通状态切换 |
| `Slow` | 180ms | 选中、开关切换、导航指示条 |

- 曲线：`CubicEase`，`EasingMode=EaseOut`；
- 需要轻微回弹时：释放阶段可用 `BackEase`（`Amplitude=0.15`），但只用于释放，不用于按下；
- 动效档位：
  - `expressive`：完整时长与幅度；
  - `subtle`：时长 ×0.62，位移幅度 ×0.5；
  - `off`：0ms，直接切到目标状态。

### 2.1 状态叠加层颜色（按明暗主题区分）

| 明暗主题 | Hover 叠加 | Press 叠加 |
|---|---|---|
| 浅色 | 黑色 8% (`#14000000`) | 黑色 14% (`#24000000`) |
| 深色 | 白色 12% (`#1FFFFFFF`) | 白色 20% (`#33FFFFFF`) |

- 直接复用 `GhostHoverBrush` / `GhostPressedBrush`（它们已按明暗主题生成黑/白叠加层），不要写死颜色；
- Ghost / Danger 按钮使用各自现有的交互画刷（`GhostHoverBrush` / `GhostPressedBrush` / `DangerHoverBrush` / `DangerPressedBrush`），叠加层本身仍只动画 `Opacity`。


## 3. 组件分类与状态矩阵

| 组件 | Hover | Press | Selected / Checked | Disabled |
|---|---|---|---|---|
| 主按钮（PrimaryButton） | 悬停层淡入 `Base` | 按下层加深 `Fast` | — | 不响应 |
| 次按钮（SecondaryButton） | 悬停层淡入 `Base` | 按下层 `Fast` | — | 不响应 |
| 文字 / 链接 / 图标按钮 | 悬停底色淡入 `Base` | 按下底色 `Fast` | — | 不响应 |
| 开关（CheckBox 样式） | 轨道轻微反馈 | — | 轨道强调层淡入 + 滑块平移 `Slow` | 不响应 |
| 左侧导航项（NavItemStyle） | 背景层淡入 `Base` | — | 背景层淡入 + 左侧竖条 `Slow` | 不响应 |
| 服务列表项（Providers ListBoxItem） | 卡片背景层淡入 `Base` | — | 背景/边框层淡入 `Slow` | 不响应 |

规则：

- 按钮**不做缩放**、不做旋转、不做布局位移；
- 只允许动画背景/指示层的 `Opacity`，以及开关滑块的 `TranslateTransform.X`；
- 文字和图标本身不参与透明度动画，避免发虚。

## 4. 模板部件契约

模板通过固定命名部件参与动效；统一行为层按部件名查找，不关心具体控件或页面。

| 控件类型 | 必须部件 | 用途 |
|---|---|---|
| ButtonBase（实心、幽灵、危险） | `PART_HoverLayer`、`PART_PressedLayer` | 悬停/按下背景层 |
| ListBoxItem / 导航项 | `PART_HoverLayer`、`PART_SelectedLayer`、`PART_Indicator` | 悬停、选中、左侧竖条 |
| CheckBox 开关 | `PART_TrackOn`、`PART_Thumb`、`PART_Track` | 强调色轨道层、滑块、轨道 hover 反馈 |

约定：

- 部件的 `Background` 继续用 `{DynamicResource ...}`，行为只动画 `Opacity`，换主题不受影响；
- 部件缺失时动效静默跳过，不抛异常、不影响功能；
- 模板里不再用 `Trigger` 直接切换这些部件的颜色/可见性，改由统一行为驱动。

## 5. 统一执行层（已实现）

新增 `ControlMotion` 附加行为，一个代码路径处理所有控件：

1. 样式设置 `ControlMotion.Enabled=True`（按钮、列表项、开关由全局样式统一设置）；
2. 行为在模板加载/更换后查找 `PART_*`，按控件类型订阅状态变化：
   - `ButtonBase`：`IsMouseOver`、`IsPressed`、`IsEnabled`；
   - `ToggleButton`/`CheckBox`：`IsChecked`、`IsMouseOver`、`IsEnabled`；
   - `ListBoxItem`：`IsMouseOver`、`IsSelected`、`IsEnabled`；
3. 状态变化时按 token 计算目标值，用 `Motion` 的曲线做 `BeginAnimation`；
4. `Motion` 档位变化时，把所有已挂载控件直接对到当前状态目标值，避免残留动画。

## 6. 状态与降级规则

| 情况 | 行为 |
|---|---|
| `Motion.off` | 0ms 直接切到目标值 |
| `Motion.subtle` | 时长 ×0.62，位移 ×0.5 |
| 控件禁用 | 立即回到静态状态，不播放动画 |
| 部件缺失 | 跳过动画，保持静态 |
| 主题切换 | 只换 Layer 的颜色（`DynamicResource`），动画状态不受影响 |
| 页面卸载 / 模板更换 | 清理动画并重新解析部件 |

## 7. 禁止事项

- 禁止给单个按钮/列表项写独立动画代码；
- 禁止在 XAML `Storyboard` 里硬编码时长和曲线（必须走 `Motion` token）；
- 禁止用按钮整体 `Opacity` 代替背景层动画；
- 禁止按钮缩放、旋转、位移动画（`off` 档和低性能环境更容易出问题）；
- 禁止动画文字/图标的颜色与透明度。

## 8. 新组件接入清单

1. 判断组件类别（按钮 / 列表 / 开关 / 其它）；
2. 样式加上 `ControlMotion.Enabled=True`；
3. 模板提供对应 `PART_*` 部件，颜色用主题资源；
4. 删除模板里针对这些状态的即时 `Trigger` 切换；
5. 在 XAML 验证脚本中检查部件名存在；
6. 手动验证 `expressive` / `subtle` / `off` 三档。

## 9. 验收与回归

- 所有内置按钮、开关、导航、服务列表在 hover/按下/选中时都走同一套缓动；
- `off` 下所有状态瞬时切换；
- 主题切换、页面切换、窗口重开后动效仍正常；
- 新增控件只要套契约就自动生效，无需修改 `ControlMotion`（新增控件类型只需补一条状态读取适配）。

## 10. 已确定

- 主按钮 hover/press 叠加层按明暗主题区分：浅色用黑色叠加（8% / 14%），深色用白色叠加（12% / 20%），复用 `GhostHoverBrush` / `GhostPressedBrush`；
- HUD 使用同一套动效，不单独降低幅度；
- 不接入 Windows「减少动画」系统设置；需要关闭动效时使用应用内 `off` 档。
