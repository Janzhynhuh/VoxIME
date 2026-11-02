using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace VoxIME.Live2D;

/// <summary>
/// 桌宠窗口的 Win32 互操作：点击穿透（WS_EX_TRANSPARENT + WM_NCHITTEST=HTTRANSPARENT）等。
/// </summary>
internal static class WindowInterop
{
    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;

    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsPopup = unchecked((int)0x80000000);

    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>读取当前鼠标指针的屏幕物理像素坐标（用于眼动跟随）。</summary>
    public static bool TryGetCursorPosition(out int x, out int y)
    {
        x = y = 0;
        if (!GetCursorPos(out var point))
        {
            return false;
        }

        x = point.X;
        y = point.Y;
        return true;
    }

    /// <summary>读取窗口在屏幕上的物理像素矩形（用于开发期截图验证）。</summary>
    public static bool TryGetWindowRect(IntPtr hwnd, out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        left = rect.Left;
        top = rect.Top;
        width = Math.Max(0, rect.Right - rect.Left);
        height = Math.Max(0, rect.Bottom - rect.Top);
        return width > 0 && height > 0;
    }

    /// <summary>把窗口标记为工具窗口：不出现在 Alt+Tab 列表。</summary>
    public static void ApplyToolWindowStyle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        var desired = exStyle | WsExToolWindow | WsExNoActivate;
        if (desired != exStyle)
        {
            _ = SetWindowLong(hwnd, GwlExStyle, desired);
        }

        // 确认使用弹出式窗口样式（无边框无任务栏时更稳定，避免被当作主窗口）。
        var style = GetWindowLong(hwnd, GwlStyle);
        if ((style & WsPopup) == 0)
        {
            _ = SetWindowLong(hwnd, GwlStyle, style | WsPopup);
        }
    }

    /// <summary>用 Win32 重新断言置顶（WPF Topmost 偶发被其它顶层窗口/远程桌面覆盖时使用）。</summary>
    public static void SetTopmost(IntPtr hwnd, bool topmost)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            hwnd,
            topmost ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    /// <summary>开启/关闭整窗点击穿透。</summary>
    public static void SetClickThrough(IntPtr hwnd, bool clickThrough)    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        var desired = clickThrough ? exStyle | WsExTransparent : exStyle & ~WsExTransparent;
        if (desired != exStyle)
        {
            _ = SetWindowLong(hwnd, GwlExStyle, desired);
        }
    }

    /// <summary>WM_NCHITTEST 钩子：穿透时把命中测试交还给下层窗口。</summary>
    public static bool TryHandleHitTest(ref nint result, bool clickThrough)
    {
        if (!clickThrough)
        {
            return false;
        }

        result = HtTransparent;
        return true;
    }
}
