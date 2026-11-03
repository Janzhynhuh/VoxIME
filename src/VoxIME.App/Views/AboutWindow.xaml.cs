using System.Windows;
using System.Windows.Input;
using VoxIME.App.Infrastructure;
using VoxIME.Core;

namespace VoxIME.App.Views;

/// <summary>关于窗口：版本、许可与数据目录入口。</summary>
public partial class AboutWindow : Window
{
    private bool _closing;
    private bool _allowClose;

    public AboutWindow()
    {
        InitializeComponent();
        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = $"{AppInfo.GetVersionLabel()}" +
                           (version is null ? "" : $" · 构建 {version.Major}.{version.Minor}.{version.Build}");
        Loaded += (_, _) => Motion.PlayEnter(RootBorder, offsetY: 14, fromScale: 0.97, duration: TimeSpan.FromMilliseconds(240));
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        CloseWithMotion();
    }

    private void CloseWithMotion()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Motion.PlayExit(RootBorder, offsetY: 8, toScale: 0.98, duration: TimeSpan.FromMilliseconds(150), completed: () =>
        {
            _allowClose = true;
            Close();
        });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseWithMotion();

    private void OpenConfigButton_Click(object sender, RoutedEventArgs e) =>
        WindowHelper.OpenFolder(AppInfo.AppDataDirectory);

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) =>
        WindowHelper.OpenFolder(AppInfo.DefaultLogDirectory);
}
