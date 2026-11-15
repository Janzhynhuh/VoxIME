using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using VoxIME.App.ViewModels;
using VoxIME.App.Views;
using VoxIME.Core.Services;
using VoxIME.Live2D;

namespace VoxIME.App.Views.SettingsPages;

/// <summary>
/// 设置页：桌宠（模型、位置预览、外观、点击穿透、动作与表情映射）。
///
/// 顶部「模型位置预览」使用独立的 Live2DPreviewHost（第二个 WebView2）：
/// 提供 60fps 的模型渲染和拖拽调整偏移；设置页不可见/切页时立即释放，避免常驻内存。
/// </summary>
public partial class Live2DPage : UserControl
{
    private SettingsViewModel? _viewModel;
    private SettingsWindow? _owner;
    private Live2DPreviewHost? _previewHost;
    private int _generation;
    private bool _syncingFromPreview;
    private (double Width, double Height)? _modelSize;
    private bool _ownerHooked;
    private bool _previewCollapsed;

    public Live2DPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        PreviewBody.SizeChanged += (_, _) => RefreshPreviewLayout();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (IsLoaded && IsVisible)
        {
            RestartPreview();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _owner = Window.GetWindow(this) as SettingsWindow;
        if (_owner is not null && !_ownerHooked)
        {
            _owner.IsVisibleChanged += OnOwnerVisibleChanged;
            _ownerHooked = true;
        }

        if (IsVisible)
        {
            RestartPreview();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_owner is not null && _ownerHooked)
        {
            _owner.IsVisibleChanged -= OnOwnerVisibleChanged;
            _ownerHooked = false;
        }

        ShutdownPreview();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            if (IsLoaded)
            {
                RestartPreview();
            }
        }
        else
        {
            ShutdownPreview();
        }
    }

    private void OnOwnerVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_owner is null || _owner.IsVisible)
        {
            if (IsLoaded && IsVisible)
            {
                RestartPreview();
            }
        }
        else
        {
            ShutdownPreview();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.ModelDirectory):
            case nameof(SettingsViewModel.SelectedLive2DModel):
                RestartPreview();
                break;
            case nameof(SettingsViewModel.Live2DWidth):
            case nameof(SettingsViewModel.Live2DHeight):
            case nameof(SettingsViewModel.Live2DScale):
            case nameof(SettingsViewModel.AutoFitWindow):
                RefreshPreviewLayout();
                PushGeometryToPreview();
                break;
            case nameof(SettingsViewModel.ModelOffsetX):
            case nameof(SettingsViewModel.ModelOffsetY):
                if (!_syncingFromPreview)
                {
                    PushGeometryToPreview();
                }

                break;
        }
    }

    private void RestartPreview()
    {
        ShutdownPreview();

        if (_previewCollapsed || _viewModel is null || !IsVisible)
        {
            return;
        }

        var modelDirectory = (_viewModel.ModelDirectory ?? string.Empty).Trim();
        var catalog = Live2DModelAssetCatalog.Load(modelDirectory);
        var modelPath = catalog.ModelJsonPath;
        if (string.IsNullOrWhiteSpace(modelDirectory)
            || string.IsNullOrWhiteSpace(modelPath)
            || !File.Exists(modelPath))
        {
            ShowPlaceholder("当前使用内置兜底形象，或模型目录里没有 .model3.json，无法显示位置预览。");
            return;
        }

        var webRoot = Live2DPreviewHost.ResolveWebRoot();
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            ShowPreviewError("找不到 Live2D 网页资源目录，无法显示位置预览。");
            _viewModel.IsPositionFallbackVisible = true;
            return;
        }

        var logger = _owner?.Logger;
        if (logger is null)
        {
            ShowPreviewError("无法获取日志服务，位置预览暂不可用。");
            _viewModel.IsPositionFallbackVisible = true;
            return;
        }

        var generation = ++_generation;
        var host = new Live2DPreviewHost(logger);
        _previewHost = host;
        host.Ready += OnPreviewReady;
        host.ModelLoaded += OnPreviewModelLoaded;
        host.OffsetChanged += OnPreviewOffsetChanged;
        host.Error += OnPreviewError;

        PreviewHostContent.Content = host.View;
        PreviewStatusText.Text = "预览加载中…";
        PreviewStatusText.Visibility = Visibility.Visible;
        PreviewErrorText.Visibility = Visibility.Collapsed;
        _viewModel.IsPositionFallbackVisible = false;
        RefreshPreviewLayout();
        PushGeometryToPreview();

        _ = InitializePreviewAsync(host, webRoot, modelDirectory, modelPath, generation);
    }

    private async Task InitializePreviewAsync(
        Live2DPreviewHost host,
        string webRoot,
        string modelDirectory,
        string modelPath,
        int generation)
    {
        try
        {
            var ok = await host.InitializeAsync(webRoot, modelDirectory, modelPath);
            if (generation != _generation || !ReferenceEquals(host, _previewHost))
            {
                host.Dispose();
                return;
            }

            if (!ok)
            {
                ShowPreviewError(host.LastError ?? "位置预览初始化失败。");
                if (_viewModel is not null)
                {
                    _viewModel.IsPositionFallbackVisible = true;
                }
            }
            else
            {
                RefreshPreviewLayout();
                PushGeometryToPreview();
            }
        }
        catch (Exception ex)
        {
            if (generation != _generation || !ReferenceEquals(host, _previewHost))
            {
                host.Dispose();
                return;
            }

            ShowPreviewError("位置预览初始化失败：" + ex.Message);
            if (_viewModel is not null)
            {
                _viewModel.IsPositionFallbackVisible = true;
            }
        }
    }

    private void OnPreviewReady(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _previewHost))
        {
            return;
        }

        PreviewStatusText.Visibility = Visibility.Collapsed;
        PreviewErrorText.Visibility = Visibility.Collapsed;
        RefreshPreviewLayout();
        PushGeometryToPreview();
    }

    private void OnPreviewModelLoaded(object? sender, VoxIME.Core.Abstractions.Live2DModelSizeEventArgs e)
    {
        if (!ReferenceEquals(sender, _previewHost))
        {
            return;
        }

        _modelSize = e.Width > 0 && e.Height > 0 ? (e.Width, e.Height) : null;
        PreviewStatusText.Visibility = Visibility.Collapsed;
        RefreshPreviewLayout();
    }

    private void OnPreviewOffsetChanged(object? sender, Live2DPreviewOffsetEventArgs e)
    {
        if (!ReferenceEquals(sender, _previewHost) || _viewModel is null)
        {
            return;
        }

        _syncingFromPreview = true;
        try
        {
            _viewModel.ModelOffsetX = e.OffsetX;
            _viewModel.ModelOffsetY = e.OffsetY;
        }
        finally
        {
            _syncingFromPreview = false;
        }
    }

    private void OnPreviewError(object? sender, string message)
    {
        if (!ReferenceEquals(sender, _previewHost))
        {
            return;
        }

        ShowPreviewError(message);
        if (_viewModel is not null)
        {
            _viewModel.IsPositionFallbackVisible = true;
        }
    }

    private void RefreshPreviewLayout()
    {
        if (_viewModel is null || _previewCollapsed)
        {
            return;
        }

        var width = Math.Max(1.0, _viewModel.Live2DWidth);
        var height = Math.Max(1.0, _viewModel.Live2DHeight);
        if (_viewModel.AutoFitWindow && _modelSize is { } size)
        {
            var fitted = Live2DWindowSizing.FitToModel(
                (int)width,
                (int)height,
                size.Width,
                size.Height,
                _viewModel.Live2DScale);
            width = fitted.Width;
            height = fitted.Height;
        }

        // 预览高度按窗口高度映射到右侧面板（800 及以上占满），再按窗口宽高比算宽度：
        // 这样即使 AutoFitWindow 让宽高比例不变，拖动高度也能明显看到预览变大/变小。
        PreviewSizeText.Text = $"窗口 {width:0}×{height:0}";

        var maxWidth = PreviewBody.ActualWidth > 8 ? PreviewBody.ActualWidth - 8 : 260;
        var maxHeight = PreviewBody.ActualHeight > 8 ? PreviewBody.ActualHeight - 8 : 380;
        var targetHeight = maxHeight * Math.Clamp(height / 800.0, 0.5, 1.0);
        var (previewWidth, previewHeight) = Live2DPreviewLayout.Fit(width, height, maxWidth, targetHeight);

        PreviewFrame.Width = Math.Max(40, previewWidth);
        PreviewFrame.Height = Math.Max(40, previewHeight);
    }

    private void PushGeometryToPreview()
    {
        if (_previewHost is null || _viewModel is null)
        {
            return;
        }

        _previewHost.ApplyGeometry(_viewModel.Live2DScale, _viewModel.ModelOffsetX, _viewModel.ModelOffsetY);
    }

    private void CollapsePreview_Click(object sender, RoutedEventArgs e) => SetPreviewCollapsed(true);

    private void ExpandPreview_Click(object sender, RoutedEventArgs e) => SetPreviewCollapsed(false);

    /// <summary>
    /// 收起/展开右侧位置预览。收起后让左侧设置项恢复全宽，并释放预览 WebView2；
    /// 展开时重新初始化，避免隐藏的预览继续占用内存/GPU。
    /// </summary>
    private void SetPreviewCollapsed(bool collapsed)
    {
        if (_previewCollapsed == collapsed)
        {
            return;
        }

        _previewCollapsed = collapsed;
        PreviewPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        PreviewRail.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;

        if (collapsed)
        {
            ShutdownPreview();
            return;
        }

        if (IsLoaded && IsVisible)
        {
            RestartPreview();
        }
    }

    private void ShowPlaceholder(string message)
    {
        _previewHost?.Dispose();
        _previewHost = null;
        _modelSize = null;
        PreviewHostContent.Content = null;
        PreviewFrame.Width = 220;
        PreviewFrame.Height = 140;
        PreviewSizeText.Text = "窗口尺寸";
        PreviewStatusText.Text = message;
        PreviewStatusText.Visibility = Visibility.Visible;
        PreviewErrorText.Visibility = Visibility.Collapsed;
        if (_viewModel is not null)
        {
            _viewModel.IsPositionFallbackVisible = false;
        }
    }

    private void ShowPreviewError(string message)
    {
        PreviewStatusText.Text = "位置预览不可用";
        PreviewStatusText.Visibility = Visibility.Visible;
        PreviewErrorText.Text = message;
        PreviewErrorText.Visibility = Visibility.Visible;
        if (_viewModel is not null)
        {
            _viewModel.IsPositionFallbackVisible = true;
        }
    }

    private void ShutdownPreview()
    {
        _generation++;

        if (_previewHost is not null)
        {
            _previewHost.Ready -= OnPreviewReady;
            _previewHost.ModelLoaded -= OnPreviewModelLoaded;
            _previewHost.OffsetChanged -= OnPreviewOffsetChanged;
            _previewHost.Error -= OnPreviewError;
            PreviewHostContent.Content = null;
            _previewHost.Dispose();
            _previewHost = null;
        }

        _modelSize = null;
        if (_viewModel is not null)
        {
            _viewModel.IsPositionFallbackVisible = false;
        }

        PreviewStatusText.Text = "预览加载中…";
        PreviewStatusText.Visibility = Visibility.Visible;
        PreviewErrorText.Visibility = Visibility.Collapsed;
    }
}
