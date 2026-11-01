using System;
using System.Collections.Generic;
using System.IO;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Live2D.DevTools;

/// <summary>
/// 无 WPF Application 场景验证：在没有 Application.Current 的进程里调用 Live2DController，
/// 必须安全降级（不崩溃、LastError 明确、IsAvailable=false）。
/// </summary>
internal static class NoApplicationSmoke
{
    public static void Run(Dictionary<string, string> options)
    {
        var outDirectory = Program.GetOption(options, "out")
                           ?? Path.Combine(AppContext.BaseDirectory, "noapp-smoke");
        Directory.CreateDirectory(outDirectory);
        var resultPath = Program.GetOption(options, "result")
                         ?? Path.Combine(outDirectory, "live2d-noapp.json");

        var logger = new FileLogger(Path.Combine(Path.GetTempPath(), "voxime-live2d-tools-logs"), LogLevel.Debug);
        var errors = new List<string>();
        var readyCount = 0;
        var result = new Dictionary<string, object?>();
        Live2DController? controller = null;

        try
        {
            result["applicationCurrentIsNull"] = System.Windows.Application.Current is null;

            controller = new Live2DController(logger);
            controller.Ready += (_, _) => readyCount++;
            controller.ErrorOccurred += (_, message) => errors.Add(message);

            controller.Initialize(new Live2DConfig { Enabled = true });
            controller.SetState(VoxIME.Core.Abstractions.Live2DState.Listening);
            controller.SetMouthOpen(0.5);
            controller.SetGaze(0.2, -0.1);
            controller.SetBubbleText("无 Application 场景不应崩溃");
            controller.Show();
            controller.Hide();

            result["isAvailable"] = controller.IsAvailable;
            result["isVisible"] = controller.IsVisible;
            result["lastError"] = controller.LastError;
            result["errorEventCount"] = errors.Count;
            result["readyEventCount"] = readyCount;
            result["pass"] = controller.IsAvailable is false
                             && string.IsNullOrWhiteSpace(controller.LastError) is false
                             && errors.Count > 0;
            Program.Log($"noapp: IsAvailable={controller.IsAvailable}, LastError={controller.LastError}");
        }
        catch (Exception ex)
        {
            result["error"] = ex.ToString();
            result["pass"] = false;
            Program.Log("noapp 场景抛出异常: " + ex);
        }
        finally
        {
            try
            {
                controller?.Dispose();
            }
            catch (Exception ex)
            {
                Program.Log("noapp Dispose 异常: " + ex.Message);
            }

            result["success"] = result.ContainsKey("error") is false;
            Program.WriteJson(resultPath, result);
            Program.WriteTextLog(resultPath + ".log");
            Program.Log("结果已写入: " + resultPath);
        }
    }
}
