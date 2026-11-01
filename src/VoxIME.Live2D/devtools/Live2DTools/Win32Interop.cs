using System;
using System.Runtime.InteropServices;

namespace VoxIME.Live2D.DevTools;

/// <summary>开发期验证窗口样式（点击穿透/置顶/工具窗口）用的 Win32 互操作。</summary>
internal static class Win32Interop
{
    public const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExTopmost = 0x00000008;
    public const int WmNcHitTest = 0x0084;
    public const int HtTransparent = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    public static IntPtr FindWindowByTitle(string title) => FindWindow(null, title);

    public static long GetExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GwlExStyle).ToInt64() : GetWindowLong32(hwnd, GwlExStyle);

    /// <summary>向窗口发送 WM_NCHITTEST（屏幕坐标），返回 HTTRANSPARENT(-1) 表示点击穿透。</summary>
    public static int SendHitTest(IntPtr hwnd, int screenX, int screenY)
    {
        var lParam = new IntPtr((screenY << 16) | (screenX & 0xFFFF));
        return SendMessage(hwnd, WmNcHitTest, IntPtr.Zero, lParam).ToInt32();
    }
}
