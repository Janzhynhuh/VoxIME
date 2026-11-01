using System.Windows;
using VoxIME.App.Views;

namespace VoxIME.App.Infrastructure;

/// <summary>UI 线程安全的消息框/确认框辅助。</summary>
public static class Ui
{
    public static void Info(string message, string title = "VoxIME 声语")
    {
        InvokeOnUi(() => MessageDialog.ShowMessage(message, title, MessageKind.Info));
    }

    public static void Success(string message, string title = "VoxIME 声语")
    {
        InvokeOnUi(() => MessageDialog.ShowMessage(message, title, MessageKind.Success));
    }

    public static void Warn(string message, string title = "VoxIME 声语")
    {
        InvokeOnUi(() => MessageDialog.ShowMessage(message, title, MessageKind.Warning));
    }

    public static void Error(string message, string title = "出错了")
    {
        InvokeOnUi(() => MessageDialog.ShowMessage(message, title, MessageKind.Error));
    }

    public static bool Confirm(string message, string title = "请确认", string okText = "确定", string cancelText = "取消")
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return false;
        }

        if (dispatcher.CheckAccess())
        {
            return MessageDialog.ShowConfirm(message, title, MessageKind.Question, okText, cancelText);
        }

        return dispatcher.Invoke(() => MessageDialog.ShowConfirm(message, title, MessageKind.Question, okText, cancelText));
    }

    private static void InvokeOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
