using System.Runtime.CompilerServices;

// Core 的音频/热键/上屏/TTS 内部类型对单元测试可见：
// 这些能力依赖真实音频设备、键盘钩子或系统剪贴板，通过内部注入点可以让测试完全离线、确定性运行。
[assembly: InternalsVisibleTo("VoxIME.Core.Tests")]
