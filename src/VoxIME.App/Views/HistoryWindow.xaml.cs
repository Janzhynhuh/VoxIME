using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoxIME.App.Infrastructure;
using VoxIME.App.ViewModels;
using VoxIME.Core.Logging;

namespace VoxIME.App.Views;

/// <summary>历史记录窗口（单例，由 AppHost 复用）。</summary>
public partial class HistoryWindow : Window
{
    private readonly HistoryViewModel _viewModel;
    private readonly IAppLogger _logger;

    public HistoryWindow(HistoryViewModel viewModel, IAppLogger logger)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _logger = logger;
        DataContext = viewModel;
        Closing += OnClosing;
    }

    /// <summary>退出流程中允许真正关闭。</summary>
    public bool AllowClose { get; set; }

    /// <summary>窗口显示时刷新最新记录。</summary>
    public void RefreshFromStore() => _viewModel.Load();

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _viewModel.Load();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.Confirm("确定要清空全部历史记录吗？该操作不可恢复。", "清空历史记录", "清空", "取消"))
        {
            _viewModel.ClearAll();
        }
    }

    private void RecordsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        var record = listBox.SelectedItem as VoxIME.Core.Abstractions.TranscriptionRecord;
        if (record is null || string.IsNullOrEmpty(record.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(record.Text);
            _viewModel.ReportStatus("已复制到剪贴板");
        }
        catch (Exception ex)
        {
            _logger.Warn($"复制历史记录失败：{ex.Message}");
            _viewModel.ReportStatus("复制失败：剪贴板可能被占用");
        }
    }
}
