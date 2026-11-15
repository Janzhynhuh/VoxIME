using System.Drawing;
using System.Windows.Forms;
using VoxIME.App.Infrastructure;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Appearance;
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
    private readonly ThemeMenuColorTable _menuColorTable = new();
    private readonly ToolStripMenuItem _live2DItem;
    private readonly ToolStripMenuItem _clickThroughItem;
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
        Action exitRequested,
        Action togglePetClickThrough,
        Action interact)
    {
        _controller = controller;
        _logger = logger;

        _live2DItem = new ToolStripMenuItem("隐藏桌宠");
        _live2DItem.Click += (_, _) => RunOnUi(ToggleLive2D);

        _clickThroughItem = new ToolStripMenuItem("桌宠点击穿透")
        {
            ToolTipText = "开启后鼠标可穿过桌宠点击下层窗口；关闭后可拖动桌宠"
        };
        _clickThroughItem.Click += (_, _) => RunOnUi(togglePetClickThrough);

        _pauseItem = new ToolStripMenuItem("暂停热键");
        _pauseItem.Click += (_, _) => RunOnUi(TogglePause);

        var interactItem = new ToolStripMenuItem("互动一下")
        {
            ToolTipText = "触发一次「点击桌宠」动作（也可直接单击桌宠，需关闭点击穿透）"
        };
        interactItem.Click += (_, _) => RunOnUi(interact);

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
            Renderer = new ThemeMenuRenderer(_menuColorTable),
            ShowImageMargin = false,
            ShowItemToolTips = true,
            Font = new Font("Microsoft YaHei UI", 9F),
            Padding = new Padding(4)
        };
        _menu.Items.Add(_live2DItem);
        _menu.Items.Add(_clickThroughItem);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(interactItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(historyItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(logsItem);
        _menu.Items.Add(aboutItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);
        _menu.Opening += OnMenuOpening;
        ApplyMenuTheme(ThemeManager.Current);

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

        ThemeManager.ThemeChanged += OnThemeChanged;
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
        // 与“隐藏/显示桌宠”“暂停/恢复热键”一致，用可执行动作表示当前状态。
        // 本菜单 ShowImageMargin=false 且 ShowCheckMargin 默认 false，Checked 勾选标记实际不会渲染，故不能依赖勾选状态。
        _clickThroughItem.Text = live2D.IsClickThrough ? "关闭桌宠点击穿透" : "开启桌宠点击穿透";
        _pauseItem.Text = _controller.IsPaused ? "恢复热键" : "暂停热键";
        _settingsItem.Enabled = true;
    }

    /// <summary>主题/配色变化：在 UI 线程重新生成菜单配色并刷新（正在打开的菜单也立即换色）。</summary>
    private void OnThemeChanged(object? sender, ResolvedTheme theme) => RunOnUi(() =>
    {
        if (_disposed)
        {
            return;
        }

        ApplyMenuTheme(theme);
        // 颜色表是绘制时读取的，Refresh 让已打开的右键菜单按新配色重绘。
        _menu.Refresh();
    });

    /// <summary>把当前主题的语义色应用到托盘右键菜单（背景/文字/悬停/边框）。</summary>
    private void ApplyMenuTheme(ResolvedTheme theme)
    {
        var palette = CreateMenuPalette(theme);
        _menuColorTable.Apply(palette);
        _menu.BackColor = palette.Background;
        _menu.ForeColor = palette.Text;
        ApplyItemForeColors(_menu.Items, palette.Text);
    }

    /// <summary>
    /// 遍历菜单项设置文字颜色；分隔线由渲染器绘制，跳过；子菜单递归处理。
    /// 菜单项统一存正文色，禁用态的弱化色由 <see cref="ThemeMenuRenderer"/> 绘制时决定，
    /// 这样菜单项重新启用后不会残留 TextMuted。
    /// </summary>
    private static void ApplyItemForeColors(ToolStripItemCollection items, Color textColor)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is ToolStripSeparator)
            {
                continue;
            }

            item.ForeColor = textColor;
            if (item is ToolStripDropDownItem dropDownItem)
            {
                ApplyItemForeColors(dropDownItem.DropDownItems, textColor);
            }
        }
    }

    /// <summary>
    /// 按当前主题解析托盘菜单配色：背景取 Popup（弹层语义），悬停/选中取 SurfaceHover，
    /// 边框/分隔线取 BorderStrong，正文取 Text，禁用文字取 TextMuted。
    /// </summary>
    private static MenuPalette CreateMenuPalette(ResolvedTheme theme)
    {
        var colors = theme.Colors;
        // 主题色解析失败时回退到默认配色方案同明暗模式的语义色，保证「浅底深字 / 深底浅字」始终可读。
        var fallback = ThemeCatalog.Default.For(theme.IsLight) ?? colors;

        return new MenuPalette(
            Background: ParseColor(colors.Popup, fallback.Popup, SystemColors.Window),
            Hover: ParseColor(colors.SurfaceHover, fallback.SurfaceHover, SystemColors.Highlight),
            Border: ParseColor(colors.BorderStrong, fallback.BorderStrong, SystemColors.WindowFrame),
            Text: ParseColor(colors.Text, fallback.Text, SystemColors.WindowText),
            TextMuted: ParseColor(colors.TextMuted, fallback.TextMuted, SystemColors.GrayText));
    }

    /// <summary>解析主题里的 #RRGGBB；非法/异常时回退到默认主题色，仍失败则用系统色兜底。</summary>
    private static Color ParseColor(string? hex, string fallbackHex, Color systemFallback)
    {
        if (TryParseColor(hex, out var color))
        {
            return color;
        }

        System.Diagnostics.Debug.WriteLine($"[TrayIconService] 菜单颜色「{hex}」无效，回退到默认主题色。");
        return TryParseColor(fallbackHex, out var fallbackColor) ? fallbackColor : systemFallback;
    }

    private static bool TryParseColor(string? hex, out Color color)
    {
        color = Color.Empty;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        try
        {
            color = ColorTranslator.FromHtml(hex.Trim());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayIconService] 解析菜单颜色「{hex}」失败：{ex.Message}");
            color = Color.Empty;
        }

        return !color.IsEmpty;
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
        ThemeManager.ThemeChanged -= OnThemeChanged;
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

    /// <summary>托盘右键菜单配色（随当前主题变化）。</summary>
    private readonly record struct MenuPalette(Color Background, Color Hover, Color Border, Color Text, Color TextMuted);

    /// <summary>
    /// 托盘右键菜单动态配色表：主题变化时更新字段，渲染器绘制时读取。
    /// 浅色主题浅底深字，深色主题深底浅字；悬停/选中用 SurfaceHover，边框用 BorderStrong。
    /// </summary>
    private sealed class ThemeMenuColorTable : ProfessionalColorTable
    {
        private Color _background = Color.FromArgb(0x1D, 0x1D, 0x22);
        private Color _hover = Color.FromArgb(0x24, 0x24, 0x29);
        private Color _border = Color.FromArgb(0x3E, 0x3E, 0x47);
        private Color _disabledText = Color.FromArgb(0x70, 0x70, 0x7C);

        /// <summary>禁用项文字颜色（基类固定用 SystemColors.GrayText，由 ThemeMenuRenderer 改用该色）。</summary>
        public Color DisabledText => _disabledText;

        public void Apply(MenuPalette palette)
        {
            _background = palette.Background;
            _hover = palette.Hover;
            _border = palette.Border;
            _disabledText = palette.TextMuted;
        }

        public override Color ToolStripDropDownBackground => _background;
        public override Color ImageMarginGradientBegin => _background;
        public override Color ImageMarginGradientMiddle => _background;
        public override Color ImageMarginGradientEnd => _background;
        public override Color MenuBorder => _border;
        public override Color MenuItemBorder => _border;
        public override Color MenuItemSelected => _hover;
        public override Color MenuItemSelectedGradientBegin => _hover;
        public override Color MenuItemSelectedGradientEnd => _hover;
        public override Color MenuItemPressedGradientBegin => _hover;
        public override Color MenuItemPressedGradientMiddle => _hover;
        public override Color MenuItemPressedGradientEnd => _hover;
        public override Color SeparatorDark => _border;
        public override Color SeparatorLight => _border;
        public override Color ToolStripBorder => _border;
    }

    /// <summary>
    /// 专业渲染器基础上修正禁用项文字颜色：WinForms 基类固定用 <see cref="SystemColors.GrayText"/>，
    /// 深色菜单上几乎不可读；这里改用当前主题的 TextMuted。
    /// </summary>
    private sealed class ThemeMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly ThemeMenuColorTable _colors;

        public ThemeMenuRenderer(ThemeMenuColorTable colors)
            : base(colors) => _colors = colors;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is { Enabled: false } && e.TextDirection == ToolStripTextDirection.Horizontal)
            {
                TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, _colors.DisabledText, e.TextFormat);
                return;
            }

            base.OnRenderItemText(e);
        }
    }
}
