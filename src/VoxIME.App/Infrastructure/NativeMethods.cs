using System.Runtime.InteropServices;

namespace VoxIME.App.Infrastructure;

/// <summary>WPF 外壳需要的少量 Win32 调用（不引入新包）。</summary>
internal static class NativeMethods
{
    public const int WmNcHitTest = 0x0084;
    public const int HtTransparent = -1;
    public const int GwlExStyle = -20;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Win11 窗口圆角偏好（2 = 圆角）。</summary>
    public static void TryEnableRoundedCorners(IntPtr hwnd)
    {
        try
        {
            const int dwmWindowCornerPreference = 33;
            var preference = 2;
            DwmSetWindowAttribute(hwnd, dwmWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // 旧系统忽略。
        }
    }

    /// <summary>
    /// 关闭 DWM 对窗口的圆角/边框/阴影装饰。
    /// HUD 是透明分层窗口、外观完全由内容自绘；若 DWM 仍按普通窗口绘制，
    /// 会在窗口矩形外留下一圈圆角阴影/描边伪影。
    /// </summary>
    public static void TryDisableDwmDecoration(IntPtr hwnd)
    {
        try
        {
            const int dwmWindowCornerPreference = 33;
            var preference = 1; // DWMWCP_DONOTROUND
            DwmSetWindowAttribute(hwnd, dwmWindowCornerPreference, ref preference, sizeof(int));

            const int dwmNcRenderingPolicy = 2;
            var policy = 2; // DWMNCRP_DISABLED
            DwmSetWindowAttribute(hwnd, dwmNcRenderingPolicy, ref policy, sizeof(int));

            const int dwmBorderColor = 34;
            var none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            DwmSetWindowAttribute(hwnd, dwmBorderColor, ref none, sizeof(int));
        }
        catch (Exception)
        {
            // 旧系统忽略。
        }
    }

    /// <summary>隐藏工具窗口（不进入 Alt+Tab）。</summary>
    public static void TrySetToolWindow(IntPtr hwnd)
    {
        try
        {
            var exStyle = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, exStyle | WsExToolWindow);
        }
        catch (Exception)
        {
            // 忽略。
        }
    }
}
