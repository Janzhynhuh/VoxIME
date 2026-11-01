// ==========================================================================
// VoxIME Live2D 开发期验证/截图工具（不属于 VoxIME.Live2D 库，不参与解决方案构建）
//
// 用法：
//   VoxIME.Live2D.Tools.exe avatar   --out <目录>
//   VoxIME.Live2D.Tools.exe pet      --model <模型目录> --out <截图.png> [--result <结果.json>]
//   VoxIME.Live2D.Tools.exe fallback --out <截图.png> [--result <结果.json>]
//
// 说明：
//   - pet/fallback 会真实创建 WPF Application + Live2DController，启动透明置顶桌宠窗口，
//     用 GDI 截屏抓取“桌宠窗口压在真实桌面壁纸上的画面”，并通过隐藏窗口前后像素对比
//     验证窗口是否真正 per-pixel 透明（cornerTransparent）。
//   - fallback 通过环境变量 VOXIME_LIVE2D_DISABLE_WEBVIEW=1 强制走内置 WPF 兜底形象。
//   - 所有结果同时打印到控制台（若可附着）并写入 JSON，便于 CI/报告引用。
// ==========================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace VoxIME.Live2D.DevTools;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private const string Usage = """
        VoxIME Live2D 开发期验证工具
        用法:
          VoxIME.Live2D.Tools.exe avatar   --out <目录>
          VoxIME.Live2D.Tools.exe pet      --model <模型目录> --out <截图.png> [--result <结果.json>] [--idlemotion 0|1]
          VoxIME.Live2D.Tools.exe fallback [--model <模型目录>] --out <截图.png> [--result <结果.json>]
          VoxIME.Live2D.Tools.exe noapp    --out <目录>
        参数:
          --out     输出目录或 PNG 路径
          --model   含 *.model3.json 的模型目录（pet 模式使用）
          --result  结果 JSON 路径（默认与截图同目录）
          --idlemotion 0 可关闭待机动作（便于截取睁眼静态图）
        """;

    private static readonly List<string> LogBuffer = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [STAThread]
    private static void Main()
    {
        try
        {
            _ = SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
        }
        catch (Exception)
        {
            // DPI 感知设置失败不影响功能。
        }

        try
        {
            _ = AttachConsole(-1);
        }
        catch (Exception)
        {
            // 无父控制台时忽略。
        }

        var rawArgs = Environment.GetCommandLineArgs();
        var args = rawArgs.Length > 1 ? rawArgs.Skip(1).ToArray() : Array.Empty<string>();
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return;
        }

        var mode = args[0].Trim().ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToArray());

        // 无 Application 场景必须在创建 WPF Application 之前验证。
        if (mode == "noapp")
        {
            NoApplicationSmoke.Run(options);
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var exitCode = 0;
            try
            {
                switch (mode)
                {
                    case "avatar":
                        AvatarShots.Run(options);
                        break;
                    case "pet":
                        await PetRun.RunAsync(options, fallbackOnly: false);
                        break;
                    case "fallback":
                        await PetRun.RunAsync(options, fallbackOnly: true);
                        break;
                    default:
                        Log($"未知模式: {mode}");
                        Console.WriteLine(Usage);
                        exitCode = 2;
                        break;
                }
            }
            catch (Exception ex)
            {
                exitCode = 1;
                Log("执行失败: " + ex);
                WriteCrashResult(options, ex);
            }
            finally
            {
                try
                {
                    app.Shutdown(exitCode);
                }
                catch (Exception)
                {
                    Environment.Exit(exitCode);
                }
            }
        };

        app.Run();
    }

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        LogBuffer.Add(line);
        try
        {
            Console.WriteLine(line);
        }
        catch (Exception)
        {
            // 没有控制台时忽略。
        }
    }

    public static string? GetOption(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = args[i + 1];
                i++;
            }
            else
            {
                result[key] = "1";
            }
        }

        return result;
    }

    public static void WriteJson(string path, object value)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, JsonSerializer.Serialize(value, JsonOptions));
        }
        catch (Exception ex)
        {
            Log($"写入 JSON 失败: {ex.Message}");
        }
    }

    public static void WriteTextLog(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(fullPath, LogBuffer);
        }
        catch (Exception)
        {
            // 忽略。
        }
    }

    private static void WriteCrashResult(Dictionary<string, string> options, Exception exception)
    {
        var path = GetOption(options, "result")
                   ?? Path.Combine(GetOption(options, "out") ?? AppContext.BaseDirectory, "live2d-tools-error.json");
        WriteJson(path, new { success = false, error = exception.ToString(), log = LogBuffer });
        WriteTextLog(path + ".log");
    }
}
