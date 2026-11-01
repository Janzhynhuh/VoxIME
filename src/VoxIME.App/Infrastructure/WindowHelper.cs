using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VoxIME.App.Infrastructure;

/// <summary>窗口/进程/UI 线程相关的通用辅助。</summary>
public static class WindowHelper
{
    /// <summary>让无边框窗口可通过指定区域拖动。</summary>
    public static void EnableDrag(Window window, UIElement handle)
    {
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && window.IsVisible)
            {
                try
                {
                    window.DragMove();
                }
                catch (InvalidOperationException)
                {
                    // 拖动期间窗口被关闭时忽略。
                }
            }
        };
    }

    /// <summary>在 UI 线程执行；已在 UI 线程则直接执行。</summary>
    public static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(new Action(action), DispatcherPriority.Normal);
    }

    /// <summary>使用资源管理器打开目录（不存在时先创建）。</summary>
    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowHelper] 打开目录失败：{ex}");
        }
    }

    /// <summary>用默认浏览器打开链接。</summary>
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowHelper] 打开链接失败：{ex}");
        }
    }

    /// <summary>主屏幕工作区（用于 HUD 底部居中定位）。</summary>
    public static Rect GetPrimaryWorkArea()
    {
        try
        {
            var workArea = SystemParameters.WorkArea;
            if (workArea.Width > 0 && workArea.Height > 0)
            {
                return workArea;
            }
        }
        catch (Exception)
        {
            // 回退默认值。
        }

        return new Rect(0, 0, 1920, 1080);
    }

    /// <summary>窗口图标（取 exe 内嵌图标）。</summary>
    public static ImageSource? LoadWindowIcon() => AppIcons.GetWindowIcon();
}

/// <summary>应用图标（从可执行文件内嵌资源提取，无需打包图片资源）。</summary>
public static class AppIcons
{
    private static ImageSource? _windowIcon;
    private static bool _windowIconLoaded;

    public static ImageSource? GetWindowIcon()
    {
        if (_windowIconLoaded)
        {
            return _windowIcon;
        }

        _windowIconLoaded = true;
        try
        {
            using var icon = LoadIcon();
            if (icon is null)
            {
                return null;
            }

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            _windowIcon = source;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppIcons] 读取窗口图标失败：{ex.Message}");
        }

        return _windowIcon;
    }

    /// <summary>创建托盘图标（调用方负责 Dispose）。不可用时返回系统默认图标副本。</summary>
    public static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var icon = LoadIcon();
            if (icon is not null)
            {
                return icon;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppIcons] 读取托盘图标失败：{ex.Message}");
        }

        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private static System.Drawing.Icon? LoadIcon()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return null;
        }

        // ExtractAssociatedIcon 会加载 exe 中由 <ApplicationIcon> 嵌入的图标。
        var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        return extracted is null ? null : new System.Drawing.Icon(extracted, 32, 32);
    }
}

