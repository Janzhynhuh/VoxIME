using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VoxIME.Live2D;

/// <summary>
/// 桌宠气泡：WPF 层渲染（半透明深色卡片 + 紫色描边 + 淡入淡出）。
/// 用于 <see cref="ILive2DController.SetBubbleText"/>，避免与 Live2D 画面抢焦点。
/// </summary>
internal sealed class BubbleView : Grid
{
    private static readonly Brush BackgroundBrush = CreateFrozenBrush(Color.FromArgb(0xF2, 0x24, 0x22, 0x34));
    private static readonly Brush BorderBrush = CreateFrozenBrush(Color.FromArgb(0x8C, 0x7C, 0x5C, 0xFF));
    private static readonly Brush TextBrush = CreateFrozenBrush(Color.FromArgb(0xFF, 0xE8, 0xE8, 0xF0));

    private readonly Border _border;
    private readonly TextBlock _text;
    private readonly TranslateTransform _translate;
    private string _currentText = "";

    public BubbleView()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(10, 10, 10, 0);
        MaxWidth = 360;
        IsHitTestVisible = false;
        Opacity = 0;

        _translate = new TranslateTransform(0, -6);
        RenderTransform = _translate;

        _text = new TextBlock
        {
            Text = "",
            Foreground = TextBrush,
            FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI"),
            FontSize = 13.5,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.None
        };

        _border = new Border
        {
            Background = BackgroundBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(13, 9, 13, 9),
            Child = _text,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.45,
                Color = Color.FromArgb(0xFF, 0x1A, 0x10, 0x40)
            }
        };

        Children.Add(_border);
    }

    public string CurrentText => _currentText;

    /// <summary>显示气泡文字；null/空白时隐藏。</summary>
    public void ShowText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Hide();
            return;
        }

        var value = text!.Trim();
        _currentText = value;
        _text.Text = value;

        // 宽度自适应：窗口窄时换行，避免溢出。
        var maxWidth = Math.Max(120, ActualWidth > 0 ? ActualWidth : 340);
        _border.MaxWidth = maxWidth;

        Visibility = Visibility.Visible;
        _translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    public void Hide()
    {
        if (Opacity <= 0.01 && string.IsNullOrEmpty(_currentText))
        {
            return;
        }

        _currentText = "";
        _translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -6, TimeSpan.FromMilliseconds(160)));
        var animation = new DoubleAnimation(Opacity <= 0.01 ? 0 : 1, 0, TimeSpan.FromMilliseconds(160));
        animation.Completed += (_, _) =>
        {
            if (string.IsNullOrEmpty(_currentText))
            {
                Visibility = Visibility.Collapsed;
            }
        };
        BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>窗口尺寸变化时限制最大宽度。</summary>
    public void UpdateMaxWidth(double windowWidth)
    {
        MaxWidth = Math.Max(140, windowWidth - 20);
        _border.MaxWidth = MaxWidth;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
