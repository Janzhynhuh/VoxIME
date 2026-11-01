using System.Drawing;
using System.Windows.Forms;
using VoxIME.App.Infrastructure;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Logging;

namespace VoxIME.App.Services;

/// <summary>
/// 系统托盘图标（WinForms NotifyIcon）。
/// 所有菜单回调都是 UI 线程安全封送的委托；NotifyIcon 事件可能在 WinForms 消息线程触发，统一 Dispatcher。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _live2DItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly IVoiceInputController _controller;
    private readonly IAppLogger _logger;
    private readonly Icon _icon;
    private bool _disposed;

    public TrayIconService(
        IVoiceInputController controller,
        IAppLogger logger,
        Action openSettings,
        Action openHistory,
        Action openAbout,
        Action openLogs,
        Action exitRequested)
    {
        _controller = controller;
        _logger = logger;

        _live2DItem = new ToolStripMenuItem("隐藏桌宠");
        _live2DItem.Click += (_, _) => RunOnUi(ToggleLive2D);

        _pauseItem = new ToolStripMenuItem("暂停热键");
        _pauseItem.Click += (_, _) => RunOnUi(TogglePause);

        _settingsItem = new ToolStripMenuItem("设置…") { Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
        _settingsItem.Click += (_, _) => RunOnUi(openSettings);

        var historyItem = new ToolStripMenuItem("历史记录");
        historyItem.Click += (_, _) => RunOnUi(openHistory);

        var logsItem = new ToolStripMenuItem("打开日志目录");
        logsItem.Click += (_, _) => RunOnUi(openLogs);

        var aboutItem = new ToolStripMenuItem("关于 VoxIME 声语");
        aboutItem.Click += (_, _) => RunOnUi(openAbout);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => RunOnUi(exitRequested);

        _menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable()),
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9F),
            Padding = new Padding(4)
        };
        _menu.Items.Add(_live2DItem);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(historyItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(logsItem);
        _menu.Items.Add(aboutItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);
        _menu.Opening += OnMenuOpening;

        _icon = AppIcons.LoadTrayIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "VoxIME 声语 · 待机",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.DoubleClick += (_, _) => RunOnUi(openSettings);
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                RunOnUi(openSettings);
            }
        };

        _controller.StateChanged += OnStateChanged;
        UpdateTooltip();
    }

    /// <summary>显示托盘气泡提示（后台线程可调用）。</summary>
    public void ShowBalloon(string title, string text, bool isError = false)
    {
        void Show()
        {
            try
            {
                _notifyIcon.BalloonTipTitle = string.IsNullOrWhiteSpace(title) ? "VoxIME 声语" : title;
                _notifyIcon.BalloonTipText = text;
                _notifyIcon.BalloonTipIcon = isError ? ToolTipIcon.Error : ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(isError ? 6000 : 3500);
            }
            catch (Exception ex)
            {
                _logger.Debug($"托盘气泡提示失败：{ex.Message}");
            }
        }

        WindowHelper.RunOnUi(Show);
    }

    /// <summary>托盘图标是否可见（跟随 UiConfig.ShowTrayIcon）。</summary>
    public bool IsVisible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var live2D = _controller.Live2D;
        _live2DItem.Text = live2D.IsVisible ? "隐藏桌宠" : "显示桌宠";
        _pauseItem.Text = _controller.IsPaused ? "恢复热键" : "暂停热键";
        _settingsItem.Enabled = true;
    }

    private void ToggleLive2D()
    {
        try
        {
            var live2D = _controller.Live2D;
            if (live2D.IsVisible)
            {
                live2D.Hide();
            }
            else
            {
                live2D.Show();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"切换桌宠显示失败：{ex.Message}");
        }
    }

    private void TogglePause()
    {
        try
        {
            _controller.IsPaused = !_controller.IsPaused;
            UpdateTooltip();
            ShowBalloon("VoxIME 声语", _controller.IsPaused ? "热键已暂停" : "热键已恢复");
        }
        catch (Exception ex)
        {
            _logger.Warn($"切换暂停状态失败：{ex.Message}");
        }
    }

    private void OnStateChanged(object? sender, AppStateChangedEventArgs e) => UpdateTooltip();

    private void UpdateTooltip()
    {
        void Update()
        {
            try
            {
                var state = _controller.IsPaused
                    ? "已暂停"
                    : _controller.State switch
                    {
                        AppState.Listening => "聆听中…",
                        AppState.Recognizing => "识别中…",
                        AppState.Speaking => "朗读中",
                        AppState.Error => "出错了",
                        _ => "待机"
                    };

                var text = $"VoxIME 声语 · {state}";
                _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
            }
            catch (Exception)
            {
                // 托盘提示更新失败不影响主流程。
            }
        }

        WindowHelper.RunOnUi(Update);
    }

    private static void RunOnUi(Action action)
    {
        try
        {
            WindowHelper.RunOnUi(action);
        }
        catch (Exception)
        {
            // 应用退出过程中忽略。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.StateChanged -= OnStateChanged;
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _icon.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Debug($"释放托盘图标失败：{ex.Message}");
        }
    }

    /// <summary>深色托盘右键菜单配色。</summary>
    private sealed class DarkMenuColorTable : ProfessionalColorTable
    {
        private static readonly Color Background = Color.FromArgb(30, 30, 43);
        private static readonly Color Hover = Color.FromArgb(42, 36, 74);
        private static readonly Color Border = Color.FromArgb(58, 58, 82);

        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
