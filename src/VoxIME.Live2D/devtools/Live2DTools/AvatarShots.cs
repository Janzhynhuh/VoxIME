using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VoxIME.Core.Abstractions;

namespace VoxIME.Live2D.DevTools;

/// <summary>渲染 WPF 兜底形象各状态截图并拼图（不依赖真实模型/WebView2）。</summary>
internal static class AvatarShots
{
    private const int CellWidth = 360;
    private const int CellHeight = 540;

    public static void Run(Dictionary<string, string> options)
    {
        var outDirectory = Program.GetOption(options, "out")
                           ?? Path.Combine(AppContext.BaseDirectory, "avatar-shots");
        Directory.CreateDirectory(outDirectory);
        Program.Log($"输出目录: {outDirectory}");

        var states = new (Live2DState State, string Name, double Mouth, string Label)[]
        {
            (Live2DState.Idle, "idle", 0, "待机 Idle"),
            (Live2DState.Listening, "listening", 0, "聆听 Listening"),
            (Live2DState.Thinking, "thinking", 0, "思考 Thinking"),
            (Live2DState.Speaking, "speaking", 0.78, "说话 Speaking（口型）"),
            (Live2DState.Error, "error", 0, "出错 Error")
        };

        var files = new List<string>();
        var labels = new List<string>();
        var statesResult = new List<object>();

        for (var index = 0; index < states.Length; index++)
        {
            var item = states[index];
            var avatar = new FallbackAvatar
            {
                Width = CellWidth,
                Height = CellHeight,
                State = item.State,
                MouthOpen = item.Mouth,
                GazeX = item.State == Live2DState.Listening ? 0.42 : 0.18,
                GazeY = -0.12
            };

            avatar.Measure(new Size(CellWidth, CellHeight));
            avatar.Arrange(new Rect(0, 0, CellWidth, CellHeight));
            avatar.UpdateLayout();
            // 确定性动画相位：既避开眨眼闭合帧，又能看到聆听波纹/思考圆点/闪星。
            avatar.SetAnimationTime(1.35 + index * 0.37);

            var bitmap = new RenderTargetBitmap(
                CellWidth * 2,
                CellHeight * 2,
                192,
                192,
                PixelFormats.Pbgra32);
            bitmap.Render(avatar);

            var file = Path.Combine(outDirectory, $"avatar-{item.Name}.png");
            ScreenCapture.SavePng(bitmap, file);
            files.Add(file);
            labels.Add(item.Label);
            statesResult.Add(new { state = item.Name, file = Path.GetFullPath(file) });
            Program.Log($"已渲染: {file}");
        }

        var montagePath = Path.Combine(outDirectory, "live2d-wpf-fallback-states.png");
        ScreenCapture.ComposeMontage(files, labels, montagePath, CellWidth, CellHeight);
        Program.Log($"拼图已保存: {montagePath}");

        // 透明的单状态图 + 结果 JSON，便于文档引用
        var singlePath = Path.Combine(outDirectory, "live2d-wpf-fallback-avatar.png");
        File.Copy(Path.Combine(outDirectory, "avatar-idle.png"), singlePath, overwrite: true);

        var resultPath = Program.GetOption(options, "result") ?? Path.Combine(outDirectory, "avatar-shots.json");
        Program.WriteJson(resultPath, new
        {
            success = true,
            montage = Path.GetFullPath(montagePath),
            avatar = Path.GetFullPath(singlePath),
            states = statesResult,
            logFile = Path.GetFullPath(resultPath + ".log")
        });
        Program.WriteTextLog(resultPath + ".log");
    }
}
