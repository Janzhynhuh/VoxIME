using System.Windows;
using System.Windows.Input;
using VoxIME.App.Infrastructure;
using VoxIME.Core;

namespace VoxIME.App.Views;

/// <summary>关于窗口：版本、许可与数据目录入口。</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = $"{AppInfo.GetVersionLabel()}" +
                           (version is null ? "" : $" · 构建 {version.Major}.{version.Minor}.{version.Build}");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenConfigButton_Click(object sender, RoutedEventArgs e) =>
        WindowHelper.OpenFolder(AppInfo.AppDataDirectory);

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) =>
        WindowHelper.OpenFolder(AppInfo.DefaultLogDirectory);
}
