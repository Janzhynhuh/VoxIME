using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VoxIME.App.Infrastructure;

namespace VoxIME.App.Views;

public enum MessageKind
{
    Info,
    Success,
    Warning,
    Error,
    Question
}

/// <summary>深色主题的轻量消息框（替代系统 MessageBox，保持视觉统一）。</summary>
public partial class MessageDialog : Window
{
    private MessageDialog()
    {
        InitializeComponent();
    }

    public static void ShowMessage(string message, string title = "VoxIME", MessageKind kind = MessageKind.Info)
    {
        var dialog = Create(message, title, kind, showCancel: false);
        dialog.ShowDialog();
    }

    public static bool ShowConfirm(string message, string title = "VoxIME", MessageKind kind = MessageKind.Question,
        string okText = "确定", string cancelText = "取消")
    {
        var dialog = Create(message, title, kind, showCancel: true);
        dialog.OkButton.Content = okText;
        dialog.CancelOptionButton.Content = cancelText;
        return dialog.ShowDialog() == true;
    }

    private static MessageDialog Create(string message, string title, MessageKind kind, bool showCancel)
    {
        var dialog = new MessageDialog();
        dialog.Title = title;
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.CancelOptionButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        dialog.OkButton.Content = showCancel ? "确定" : "知道了";
        dialog.ApplyKind(kind);

        var owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive && w != dialog);
        if (owner is not null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog;
    }

    private void ApplyKind(MessageKind kind)
    {
        var (glyph, color) = kind switch
        {
            MessageKind.Success => ("\uE73E", "#4ADE80"),
            MessageKind.Warning => ("\uE7BA", "#FBBF24"),
            MessageKind.Error => ("\uE783", "#FF6B6B"),
            MessageKind.Question => ("\uE897", "#7C5CFF"),
            _ => ("\uE946", "#7C5CFF")
        };

        IconGlyph.Text = glyph;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        IconGlyph.Foreground = brush;
        IconBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) { Opacity = 0.16 };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
