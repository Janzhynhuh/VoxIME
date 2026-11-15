# VoxIME.NativePet

原生桌宠进程（Phase 1 骨架）。

当前阶段实现桌宠进程的基础设施，以及**一个最小 Cubism 模型渲染路径**：

- Win32 无边框、置顶、不抢焦点窗口；
- D3D11 flip swapchain + DirectComposition 逐像素透明合成；
- 点击穿透开关；
- 命名管道控制通道；
- 父进程看门狗（主程序退出/崩溃时桌宠自动退出）；
- 多显示器选择（`--monitor 2`）；
- 可选 Cubism：加载 `model3.json`/`moc3`/贴图并绘制默认姿势；
- Cubism shader 磁盘缓存 + 构建时预热，避免每次启动重新编译。

尚未接入动作/表情/物理/多 MotionManager，也还没接主程序 IPC 命令。

## 构建

需要 Visual Studio 2022/2026 的 C++ 工具链和 CMake：

```powershell
# 只构建窗口/D3D/IPC 骨架（不依赖 Cubism SDK）
.\scripts\build-native-pet.ps1

# 启用 Cubism 模型渲染（SDK 不进入仓库）
$env:VOXIME_CUBISM_SDK = 'E:\path\to\CubismSdkForNative-5-r.5'
.\scripts\build-native-pet.ps1
```

默认输出：

```text
artifacts\native-pet\Release\VoxIME.NativePet.exe
artifacts\native-pet\Release\ShaderCache\*.cso
```

启用 Cubism 时，构建脚本会把 `Live2DCubismCore.dll` 与 `FrameworkShaders/` 复制到输出目录，
并用 SDK 自带的 Hiyori 模型执行一次 `--warm-shaders`，生成设备无关的 DXBC 缓存。
不需要预热时加 `-SkipShaderPrewarm`。
SDK 路径也可以通过参数传入：`-CubismSdk 'E:\...'`。

## Shader 缓存

运行时查找顺序：

1. `VOXIME_SHADER_CACHE_DIR`（构建/调试用）
2. exe 目录 `ShaderCache/`（安装包内置，只读）
3. `%LOCALAPPDATA%\VoxIME\native-shader-cache`（运行时写入）

缓存 key 由 shader 源、entry point、profile、编译 flags 组成；未命中时回退到
`D3DCompile` 并写入本地缓存。DXBC 与具体 GPU 无关，可随安装包分发；驱动仍会在
首次创建 shader 时把 DXBC 转成 GPU 指令，但通常远快于编译 FX 源。

## 运行参数

```text
VoxIME.NativePet.exe --monitor 2 --width 482 --height 600 --model "E:\models\Hiyori.model3.json"
```

| 参数 | 说明 |
|---|---|
| `--monitor N` | 1-based 显示器编号，1=主屏 |
| `--width` / `--height` | 窗口物理像素尺寸 |
| `--pipe <name>` | 命名管道名，例如 `\\.\pipe\VoxIME.Pet.xxx` |
| `--parent-pid` | 父进程 PID；父进程退出时桌宠退出 |
| `--model <model3.json>` | 模型路径；启用 Cubism 构建时生效 |
| `--warm-shaders` | 用 `--model` 初始化一次 renderer，只生成 shader 缓存后退出 |
| `--debug-color` | 用半透明蓝色清屏，便于验证 DComp 合成；正式运行不要加 |

## IPC 帧格式

命名管道使用**长度前缀帧**，避免 StreamWriter 分片/编码差异：

```text
uint32 little-endian payload length
UTF-8 payload bytes
```

当前命令（纯文本，后续会扩展为 JSON）：

```text
ping
show
hide
shutdown
click-through true
click-through false
```

连接后服务端先发送：

```json
{"type":"ready","width":482,"height":600}
```

`ping` 返回：

```json
{"type":"pong"}
```

## 尚未实现

- 动作/表情/物理/多 MotionManager；
- C# `NativePetController` 与主程序开关；
- 模型缩放、窗口拖动、位置持久化；
- 透明区域的输入命中细节；
- 正式发布打包（当前不进入 `dotnet publish`）。

## 许可

本目录是 VoxIME 自有代码，不含 Cubism SDK。后续引入的官方示例文件必须保留其版权头与
Live2D LICENSE/NOTICE；Cubism Core 只允许按 `RedistributableFiles.txt` 分发。
