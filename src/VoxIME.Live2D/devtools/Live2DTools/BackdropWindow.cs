using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoxIME.Live2D.DevTools;

/// <summary>
/// 透明性校验背景窗：在桌宠窗口正下方垫一块静态棋盘格（普通非置顶窗口），
/// 截图后隐藏桌宠再截一次，若桌宠窗口真正 per-pixel 透明，两次截图的四角像素应完全一致。
/// </summary>
internal sealed class BackdropWindow : Window
{
    public BackdropWindow(double left, double top, double width, double height)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        Topmost = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        Background = CreateCheckerBrush();
        Content = new Grid
        {
            Children =
            {
                new TextBlock
                {
                    Text = "VoxIME 桌宠透明性校验背景（棋盘格）",
                    Foreground = new SolidColorBrush(Color.FromArgb(0x9A, 0xE8, 0xE8, 0xF0)),
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 14)
                }
            }
        };
    }

    private static Brush CreateCheckerBrush()
    {
        var drawingGroup = new DrawingGroup();
        using (var context = drawingGroup.Open())
        {
            var dark = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x30));
            var light = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x3A));
            dark.Freeze();
            light.Freeze();
            context.DrawRectangle(dark, null, new Rect(0, 0, 32, 32));
            context.DrawRectangle(light, null, new Rect(0, 0, 16, 16));
            context.DrawRectangle(light, null, new Rect(16, 16, 16, 16));
        }

        drawingGroup.Freeze();
        var brush = new DrawingBrush(drawingGroup)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 32, 32),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }
}
