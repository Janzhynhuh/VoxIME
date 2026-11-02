using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VoxIME.Core.Services;

/// <summary>一次录音开始时的前台应用上下文（只读、瞬时，不落盘）。</summary>
public sealed record AppContextInfo(string ProcessName, string AppCategory, string? WindowTitle);

/// <summary>读取当前前台应用信息，供 ASR prompt 使用；可注入 fake 做单测。</summary>
public interface IAppContextProvider
{
    /// <summary>捕获当前前台窗口；无法获取、或前台是 VoxIME 自身时返回 null。</summary>
    AppContextInfo? CaptureCurrent();
}

/// <summary>
/// Win32 前台窗口上下文：GetForegroundWindow → 进程名/窗口标题 → 友好分类。
/// 不读取窗口内容、剪贴板或选中文字。
/// </summary>
public sealed class Win32AppContextProvider : IAppContextProvider
{
    private static readonly Dictionary<string, string> CategoryByProcess = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = "代码编辑器",
        ["code - insiders"] = "代码编辑器",
        ["devenv"] = "Visual Studio",
        ["idea"] = "JetBrains IDE",
        ["idea64"] = "JetBrains IDE",
        ["pycharm"] = "JetBrains IDE",
        ["pycharm64"] = "JetBrains IDE",
        ["webstorm"] = "JetBrains IDE",
        ["rider"] = "JetBrains IDE",
        ["clion"] = "JetBrains IDE",
        ["chrome"] = "浏览器",
        ["msedge"] = "浏览器",
        ["firefox"] = "浏览器",
        ["brave"] = "浏览器",
        ["opera"] = "浏览器",
        ["wechat"] = "聊天工具",
        ["weixin"] = "聊天工具",
        ["qq"] = "聊天工具",
        ["dingtalk"] = "聊天工具",
        ["feishu"] = "聊天工具",
        ["lark"] = "聊天工具",
        ["telegram"] = "聊天工具",
        ["discord"] = "聊天工具",
        ["slack"] = "聊天工具",
        ["winword"] = "办公文档",
        ["excel"] = "办公文档",
        ["powerpnt"] = "办公文档",
        ["wps"] = "办公文档",
        ["et"] = "办公文档",
        ["wpsoffice"] = "办公文档",
        ["windowsterminal"] = "终端",
        ["wt"] = "终端",
        ["cmd"] = "终端",
        ["powershell"] = "终端",
        ["pwsh"] = "终端",
        ["explorer"] = "文件资源管理器",
        ["obsidian"] = "笔记",
        ["notion"] = "笔记",
        ["typora"] = "笔记",
        ["photoshop"] = "设计工具",
        ["illustrator"] = "设计工具",
        ["figma"] = "设计工具",
        ["applicationframehost"] = "Windows 应用"
    };

    private const int MaxTitleLength = 256;

    public AppContextInfo? CaptureCurrent()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            // 前台是 VoxIME 自己的设置/HUD 时不发送上下文，避免污染识别。
            return null;
        }

        var title = ReadWindowTitle(hwnd);
        var processName = ReadProcessName(processId, title);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var normalized = NormalizeProcessName(processName);
        if (TryGetOwnProcessName(normalized))
        {
            return null;
        }

        var category = CategoryByProcess.TryGetValue(normalized, out var mapped)
            ? mapped
            : "应用";

        return new AppContextInfo(normalized, category, title);
    }

    private static string? ReadWindowTitle(IntPtr hwnd)
    {
        try
        {
            var length = GetWindowTextLength(hwnd);
            if (length <= 0)
            {
                return null;
            }

            var builder = new StringBuilder(Math.Min(length + 1, MaxTitleLength + 1));
            _ = GetWindowText(hwnd, builder, builder.Capacity);
            var title = builder.ToString().Trim();
            return title.Length switch
            {
                0 => null,
                > MaxTitleLength => title[..MaxTitleLength],
                _ => title
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadProcessName(uint processId, string? title)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch (Exception)
        {
            // 受保护/高权限进程可能拒绝访问；退回标题中的粗略分类。
        }

        return string.IsNullOrWhiteSpace(title) ? null : InferProcessFromTitle(title);
    }

    private static string? InferProcessFromTitle(string title)
    {
        if (title.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase))
        {
            return "Code";
        }
        if (title.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase))
        {
            return "devenv";
        }
        if (title.Contains("Word", StringComparison.OrdinalIgnoreCase))
        {
            return "WINWORD";
        }
        if (title.Contains("Excel", StringComparison.OrdinalIgnoreCase))
        {
            return "EXCEL";
        }
        if (title.Contains("PowerPoint", StringComparison.OrdinalIgnoreCase))
        {
            return "POWERPNT";
        }

        return null;
    }

    private static string NormalizeProcessName(string processName)
    {
        var name = processName.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }

    private static bool TryGetOwnProcessName(string processName)
    {
        try
        {
            var current = Process.GetCurrentProcess().ProcessName;
            return string.Equals(NormalizeProcessName(current), processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);
}
