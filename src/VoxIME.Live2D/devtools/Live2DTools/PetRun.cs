using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Live2D.DevTools;

/// <summary>
/// 真实启动 <see cref="Live2DController"/>：透明桌宠窗口 + WebView2/Live2D 模型（或强制兜底形象），
/// 等待就绪后抓取“桌宠压在真实桌面壁纸上”的截屏，并用“棋盘格背景窗 + 隐藏桌宠前后像素对比”
/// 对窗口的 per-pixel 透明性做确定性校验（避免桌面壁纸动画干扰）。
/// </summary>
internal static class PetRun
{
    public static async Task RunAsync(Dictionary<string, string> options, bool fallbackOnly)
    {
        var outputPng = Program.GetOption(options, "out")
                        ?? throw new ArgumentException("缺少 --out 参数（截图输出路径）。");
        outputPng = Path.GetFullPath(outputPng);
        var resultPath = Program.GetOption(options, "result")
                         ?? Path.ChangeExtension(outputPng, ".json");
        var modelDirectory = Program.GetOption(options, "model");
        var idleMotionEnabled = Program.GetOption(options, "idlemotion") != "0";

        var logDirectory = Path.Combine(Path.GetTempPath(), "voxime-live2d-tools-logs");
        var logger = new FileLogger(logDirectory, LogLevel.Debug);
        Program.Log($"日志文件: {logger.LogFilePath}");

        var errors = new List<string>();
        var readyCount = 0;
        var readySource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Live2DController? controller = null;
        var result = new Dictionary<string, object?>();
        BackdropWindow? backdrop = null;

        try
        {
            // 验证工具使用独立的 WebView2 UserData 目录，避免与正在运行的 VoxIME 实例抢目录。
            Environment.SetEnvironmentVariable(
                "VOXIME_LIVE2D_USER_DATA_DIR",
                Path.Combine(Path.GetTempPath(), "voxime-live2d-tools-webview2"));

            if (fallbackOnly)
            {
                Environment.SetEnvironmentVariable("VOXIME_LIVE2D_DISABLE_WEBVIEW", "1");
                Program.Log("模式=fallback：强制禁用 WebView2，验证 WPF 兜底形象降级路径。");
            }
            else
            {
                Program.Log($"模式=pet：模型目录={modelDirectory ?? "(未配置，将走兜底)"}, IdleMotion={idleMotionEnabled}");
            }

            var workArea = SystemParameters.WorkArea;
            var width = Math.Min(420, Math.Max(280, workArea.Width * 0.32));
            var height = Math.Min(600, Math.Max(380, workArea.Height * 0.72));
            var left = workArea.Left + Math.Max(40, (workArea.Width - width) * 0.12);
            var top = workArea.Top + Math.Max(30, (workArea.Height - height) * 0.18);

            var config = new Live2DConfig
            {
                Enabled = true,
                ModelDirectory = modelDirectory,
                Width = width,
                Height = height,
                Left = left,
                Top = top,
                Opacity = 1.0,
                Scale = 1.0,
                ClickThrough = true,
                AlwaysOnTop = true,
                EyeFollow = true,
                IdleMotion = idleMotionEnabled,
                MouthSync = true,
                MouthSensitivity = 1.0,
                ShowBubble = true,
                IdleMotionGroup = "Idle",
                FallbackAvatarTheme = "violet"
            };

            Program.Log($"创建控制器：窗口 {width:0}x{height:0} @ ({left:0},{top:0})");
            controller = new Live2DController(logger);
            controller.Ready += (_, _) =>
            {
                readyCount++;
                Program.Log($"收到 Ready 事件（第 {readyCount} 次）。");
                readySource.TrySetResult(true);
            };
            controller.ErrorOccurred += (_, message) =>
            {
                errors.Add(message);
                Program.Log($"收到 ErrorOccurred: {message}");
            };

            controller.Initialize(config);

            var ready = await WaitAsync(readySource.Task, TimeSpan.FromSeconds(35)).ConfigureAwait(true);
            Program.Log($"Ready={ready}, IsAvailable={controller.IsAvailable}, LastError={controller.LastError}");

            // 等待画面真正渲染（模型加载完成后的动画/气泡稳定）
            await Task.Delay(2200).ConfigureAwait(true);

            controller.SetState(VoxIME.Core.Abstractions.Live2DState.Speaking);
            controller.SetMouthOpen(0.85);
            controller.SetGaze(0.65, -0.3);
            controller.SetBubbleText("你好，我是 VoxIME 桌宠～");
            await Task.Delay(2600).ConfigureAwait(true);

            if (!controller.TryGetWindowPixelBounds(out var windowLeft, out var windowTop, out var pixelWidth, out var pixelHeight))
            {
                throw new InvalidOperationException("无法读取桌宠窗口的屏幕像素矩形（窗口可能未显示）。");
            }

            Program.Log($"窗口像素矩形: ({windowLeft},{windowTop}) {pixelWidth}x{pixelHeight}");

            // ---- 1) 桌面壁纸实拍（文档主截图） ----
            using (var withWallpaper = ScreenCapture.CaptureRegion(windowLeft, windowTop, pixelWidth, pixelHeight))
            {
                ScreenCapture.SavePng(withWallpaper, outputPng);
                Program.Log($"桌宠截图已保存: {outputPng}");
            }

            // ---- 2) 隐藏窗口后同区域截图（信息性对比：壁纸可能是动态/幻灯片，仅做记录） ----
            controller.Hide();
            await Task.Delay(700).ConfigureAwait(true);
            using (var wallpaperOnly = ScreenCapture.CaptureRegion(windowLeft, windowTop, pixelWidth, pixelHeight))
            {
                using var withWallpaper = LoadBitmap(outputPng);
                var wallpaperCompare = ScreenCapture.Compare(withWallpaper, wallpaperOnly);
                result["wallpaperChangedRatio"] = Math.Round(wallpaperCompare.ChangedRatio, 4);
            }

            // ---- 3) 棋盘格背景窗：确定性透明性校验 ----
            controller.Show();
            await Task.Delay(500).ConfigureAwait(true);

            backdrop = new BackdropWindow(left, top, width, height);
            backdrop.Show();
            await Task.Delay(400).ConfigureAwait(true);

            var transparencyShot = Path.Combine(
                Path.GetDirectoryName(outputPng) ?? ".",
                Path.GetFileNameWithoutExtension(outputPng) + ".transparency.png");
            using (var withBackdrop = ScreenCapture.CaptureRegion(windowLeft, windowTop, pixelWidth, pixelHeight))
            {
                ScreenCapture.SavePng(withBackdrop, transparencyShot);

                controller.Hide();
                await Task.Delay(600).ConfigureAwait(true);
                using var backdropOnly = ScreenCapture.CaptureRegion(windowLeft, windowTop, pixelWidth, pixelHeight);
                var compare = ScreenCapture.Compare(withBackdrop, backdropOnly);
                Program.Log(
                    $"透明性校验(棋盘格背景): changedRatio={compare.ChangedRatio:0.0000} " +
                    $"({compare.ChangedPixels}/{compare.TotalPixels}), cornersEqual={compare.CornersEqual}, " +
                    $"cornerMaxDiff={compare.CornerMaxDiff}, changedBounds={compare.ChangedBounds}");

                result["changedPixelRatio"] = Math.Round(compare.ChangedRatio, 4);
                result["changedPixels"] = compare.ChangedPixels;
                result["cornerTransparent"] = compare.CornersEqual;
                result["cornerMaxDiff"] = compare.CornerMaxDiff;
                result["changedBounds"] = compare.ChangedBounds is null
                    ? null
                    : new
                    {
                        x = compare.ChangedBounds.Value.X,
                        y = compare.ChangedBounds.Value.Y,
                        width = compare.ChangedBounds.Value.Width,
                        height = compare.ChangedBounds.Value.Height
                    };
                result["transparencyCheckScreenshot"] = transparencyShot;
            }

            // ---- 4) 窗口样式 / 点击穿透切换校验（置顶、不占任务栏、穿透、可拖动入口） ----
            controller.Show();
            await Task.Delay(400).ConfigureAwait(true);
            var hwnd = Win32Interop.FindWindowByTitle("VoxIME 桌宠");
            result["windowHandleFound"] = hwnd != IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                var hitX = windowLeft + pixelWidth / 2;
                var hitY = windowTop + pixelHeight / 2;
                var exStyle = Win32Interop.GetExStyle(hwnd);
                var hit = Win32Interop.SendHitTest(hwnd, hitX, hitY);
                var clickThroughCheck = new Dictionary<string, object?>
                {
                    ["wsExTransparent"] = (exStyle & Win32Interop.WsExTransparent) != 0,
                    ["wsExToolWindow"] = (exStyle & Win32Interop.WsExToolWindow) != 0,
                    ["wsExTopmost"] = (exStyle & Win32Interop.WsExTopmost) != 0,
                    ["hitTestResult"] = hit,
                    ["hitTestIsTransparent"] = hit == Win32Interop.HtTransparent
                };
                result["clickThroughCheck"] = clickThroughCheck;

                var noClickConfig = ConfigJson.Clone(config);
                noClickConfig.ClickThrough = false;
                controller.ApplyConfig(noClickConfig);
                await Task.Delay(500).ConfigureAwait(true);
                var exStyleAfter = Win32Interop.GetExStyle(hwnd);
                var hitAfter = Win32Interop.SendHitTest(hwnd, hitX, hitY);
                result["afterDisableClickThrough"] = new Dictionary<string, object?>
                {
                    ["wsExTransparent"] = (exStyleAfter & Win32Interop.WsExTransparent) != 0,
                    ["hitTestResult"] = hitAfter,
                    ["mouseEventsReachable"] = hitAfter != Win32Interop.HtTransparent
                };
                Program.Log(
                    "窗口样式校验: WS_EX_TRANSPARENT=" + clickThroughCheck["wsExTransparent"] +
                    ", WS_EX_TOOLWINDOW=" + clickThroughCheck["wsExToolWindow"] +
                    ", WS_EX_TOPMOST=" + clickThroughCheck["wsExTopmost"] +
                    ", hitTest=" + hit + " (HTTRANSPARENT=" + (hit == Win32Interop.HtTransparent) + ")" +
                    ", 关闭穿透后 hitTest=" + hitAfter);

                // 恢复原始配置，避免影响后续退出流程
                controller.ApplyConfig(config);
            }

            result["mode"] = fallbackOnly ? "fallback" : "pet";
            result["ready"] = ready;
            result["readyEventCount"] = readyCount;
            result["isAvailable"] = controller.IsAvailable;
            result["usingFallbackAvatar"] = controller.UsingFallbackAvatar;
            result["lastError"] = controller.LastError;
            result["errors"] = errors;
            result["modelDirectory"] = modelDirectory;
            result["screenshot"] = outputPng;
            result["windowPixelBounds"] = new { x = windowLeft, y = windowTop, width = pixelWidth, height = pixelHeight };

            var transparent = result.TryGetValue("cornerTransparent", out var t) && t is true;
            if (fallbackOnly)
            {
                // 配置了模型但强制禁用 WebView2 时，必须通过 ErrorOccurred/LastError 报告降级原因；
                // 未配置模型属于正常用法，不算错误。
                var expectError = !string.IsNullOrWhiteSpace(modelDirectory);
                var errorReported = string.IsNullOrEmpty(controller.LastError) is false && errors.Count > 0;
                result["expectedError"] = expectError;
                result["pass"] = ready
                                 && controller.IsAvailable
                                 && controller.UsingFallbackAvatar
                                 && transparent
                                 && (errorReported || !expectError);
            }
            else
            {
                result["pass"] = ready
                                 && controller.IsAvailable
                                 && !controller.UsingFallbackAvatar
                                 && transparent;
            }

            Program.Log($"结果判定 pass={result["pass"]}");
        }
        catch (Exception ex)
        {
            result["error"] = ex.ToString();
            result["mode"] = fallbackOnly ? "fallback" : "pet";
            Program.Log("执行异常: " + ex);
            throw;
        }
        finally
        {
            try
            {
                backdrop?.Close();
            }
            catch (Exception)
            {
                // 忽略。
            }

            try
            {
                controller?.Dispose();
            }
            catch (Exception ex)
            {
                Program.Log("释放控制器异常: " + ex.Message);
            }

            result["success"] = result.ContainsKey("error") is false;
            Program.WriteJson(resultPath, result);
            Program.WriteTextLog(resultPath + ".log");
            Program.Log("结果已写入: " + resultPath);
            await Task.Delay(250).ConfigureAwait(true);
        }
    }

    private static System.Drawing.Bitmap LoadBitmap(string path)
    {
        using var image = System.Drawing.Image.FromFile(path);
        return new System.Drawing.Bitmap(image);
    }

    private static async Task<bool> WaitAsync(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(true);
        return completed == task;
    }
}
