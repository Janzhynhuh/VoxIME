using System.Runtime.InteropServices;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Hotkeys;

/// <summary>
/// 基于 <c>SetWindowsHookEx(WH_KEYBOARD_LL)</c> + <c>WH_MOUSE_LL</c> 的全局热键服务。
/// </summary>
/// <remarks>
/// <para>支持三类热键：按住说话（如 <c>Ctrl+Alt</c> 或 <c>MouseX1</c>，组合键全部按下→Pressed、任一抬起→Released）、
/// 按下切换（如 <c>Ctrl+Alt+M</c> 或 <c>MouseX2</c>）、取消（如 <c>Esc</c>）。</para>
/// <para>鼠标键使用 MouseX1 / MouseX2（侧键）、MouseMiddle、MouseLeft、MouseRight 表示；
/// 当鼠标事件命中已配置的热键时会吞掉该事件（避免同时触发浏览器前进/后退等系统默认行为），
/// 未配置的鼠标事件照常传递，不吞用户输入。</para>
/// <para>钩子运行在独立后台线程（自带消息循环），启动/停止/更新配置线程安全；
/// 任何失败都通过 <see cref="Error"/> 事件与日志上报，不会抛到 UI 线程之外。</para>
/// <para>钩子回调始终调用 <c>CallNextHookEx</c>（键盘与未命中的鼠标事件），绝不吞掉用户按键。</para>
/// </remarks>
public sealed class GlobalHotkeyService : IHotkeyService
{
    private const int HookThreadJoinTimeoutMs = 2000;

    private readonly IAppLogger _logger;
    private readonly object _lifecycle = new();
    private readonly object _sync = new();

    private volatile HotkeyMatcher? _matcher;
    private volatile bool _running;
    private volatile bool _stopRequested;
    private volatile bool _hookInstalled;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle;
    private IntPtr _mouseHookHandle;
    private ManualResetEventSlim? _hookReady;
    private KeyboardNativeMethods.LowLevelKeyboardProc? _hookProc;
    private KeyboardNativeMethods.LowLevelMouseProc? _mouseHookProc;
    private bool _disposed;

    public GlobalHotkeyService(IAppLogger logger)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public event EventHandler<HotkeyEventArgs>? HotkeyTriggered;

    /// <summary>错误信息（热键配置非法、钩子安装失败、钩子线程异常等）。可能在后台线程触发。</summary>
    public event EventHandler<string>? Error;

    public bool IsRunning => _running;

    public void Start(HotkeyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();
            StartCore(config);
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            StopCore();
        }
    }

    /// <summary>热更新热键配置（内部会重启钩子线程）。</summary>
    public void Update(HotkeyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            StopCore();
            StartCore(config);
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            StopCore();
            _disposed = true;
        }
    }

    private void StartCore(HotkeyConfig config)
    {
        var matcher = new HotkeyMatcher(config);
        foreach (var invalid in matcher.InvalidGestures)
        {
            ReportError($"热键配置无效，已忽略：{invalid}");
        }

        _matcher = matcher;
        _stopRequested = false;
        _hookInstalled = false;
        _hookHandle = IntPtr.Zero;
        _mouseHookHandle = IntPtr.Zero;
        _mouseHookProc = MouseHookCallback;

        var ready = new ManualResetEventSlim(false);
        _hookReady = ready;
        _hookProc = HookCallback;

        var thread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "VoxIME.GlobalHotkeyHook"
        };
        _hookThread = thread;

        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            _logger.Error("创建全局热键线程失败", ex);
            ReportError("无法创建全局热键监听线程：" + ex.Message);
            StopCore();
            return;
        }

        if (!ready.Wait(TimeSpan.FromSeconds(3)))
        {
            ReportError("启动全局热键监听超时。");
            StopCore();
            return;
        }

        if (!_hookInstalled)
        {
            // 具体原因已在线程内通过 Error 事件报告。
            StopCore();
            return;
        }

        _running = true;
        _logger.Info($"全局热键已启动：按住说话={matcher.PushToTalkGesture?.Display ?? "未启用"}，" +
                     $"按下切换={matcher.ToggleGesture?.Display ?? "未启用"}，" +
                     $"取消={matcher.CancelGesture?.Display ?? "未启用"}；" +
                     $"鼠标钩子={(_mouseHookHandle != IntPtr.Zero ? "已启用" : "不可用")}");
    }

    private void StopCore()
    {
        _stopRequested = true;
        var thread = _hookThread;
        var threadId = _hookThreadId;
        _hookThread = null;
        _hookThreadId = 0;
        _matcher = null;
        _running = false;

        if (thread is not null)
        {
            try
            {
                if (threadId != 0)
                {
                    KeyboardNativeMethods.PostThreadMessage(threadId, KeyboardNativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("向热键线程发送退出消息失败：" + ex.Message);
            }

            try
            {
                if (!thread.Join(HookThreadJoinTimeoutMs))
                {
                    _logger.Warn("全局热键钩子线程未在超时时间内退出。");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("等待全局热键线程退出时出错：" + ex.Message);
            }
        }

        var ready = _hookReady;
        _hookReady = null;
        ready?.Dispose();
        _logger.Debug("全局热键监听已停止。");
    }

    private void HookThreadMain()
    {
        try
        {
            _hookThreadId = KeyboardNativeMethods.GetCurrentThreadId();

            // 先创建线程消息队列，之后 PostThreadMessage(WM_QUIT) 才有意义。
            KeyboardNativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, KeyboardNativeMethods.PM_NOREMOVE);

            var hookProc = _hookProc;
            if (hookProc is null)
            {
                ReportError("内部错误：键盘钩子回调未初始化。");
                return;
            }

            _hookHandle = KeyboardNativeMethods.SetWindowsHookEx(
                KeyboardNativeMethods.WH_KEYBOARD_LL,
                hookProc,
                KeyboardNativeMethods.GetModuleHandle(null),
                0);

            if (_hookHandle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.Error($"安装全局键盘钩子失败，Windows 错误码 {error}。");
                ReportError($"安装全局键盘钩子失败（Windows 错误码 {error}），热键不可用。");
                return;
            }

            _hookInstalled = true;

            // 安装鼠标钩子（可选）：失败不致命，但配置了鼠标热键时需要明确上报。
            var mouseProc = _mouseHookProc;
            if (mouseProc is not null)
            {
                _mouseHookHandle = KeyboardNativeMethods.SetWindowsHookExMouse(
                    KeyboardNativeMethods.WH_MOUSE_LL,
                    mouseProc,
                    KeyboardNativeMethods.GetModuleHandle(null),
                    0);

                if (_mouseHookHandle == IntPtr.Zero)
                {
                    var mouseError = Marshal.GetLastWin32Error();
                    var message = $"安装全局鼠标钩子失败（Windows 错误码 {mouseError}），鼠标热键不可用。";
                    _logger.Error(message);
                    if (MatcherUsesMouse(_matcher))
                    {
                        ReportError(message);
                    }
                }
                else
                {
                    _logger.Debug("全局鼠标钩子安装成功（支持鼠标侧键热键）。");
                }
            }

            SignalReady();
            _logger.Debug("全局钩子线程已就绪（键盘 + 鼠标）。");

            while (!_stopRequested)
            {
                var result = KeyboardNativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0);
                if (result <= 0)
                {
                    break;
                }

                KeyboardNativeMethods.TranslateMessage(ref msg);
                KeyboardNativeMethods.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("全局键盘钩子线程异常退出", ex);
            ReportError("全局键盘钩子线程异常：" + ex.Message);
        }
        finally
        {
            var handle = _hookHandle;
            _hookHandle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                try
                {
                    KeyboardNativeMethods.UnhookWindowsHookEx(handle);
                }
                catch (Exception ex)
                {
                    _logger.Debug("注销全局键盘钩子失败：" + ex.Message);
                }
            }

            var mouseHandle = _mouseHookHandle;
            _mouseHookHandle = IntPtr.Zero;
            if (mouseHandle != IntPtr.Zero)
            {
                try
                {
                    KeyboardNativeMethods.UnhookWindowsHookEx(mouseHandle);
                }
                catch (Exception ex)
                {
                    _logger.Debug("注销全局鼠标钩子失败：" + ex.Message);
                }
            }

            _hookInstalled = false;
            SignalReady();
            _logger.Debug("全局钩子线程已退出。");
        }
    }

    /// <summary>唤醒等待启动的线程；即使事件已释放也不能让后台线程抛未处理异常。</summary>
    private void SignalReady()
    {
        try
        {
            _hookReady?.Set();
        }
        catch (ObjectDisposedException)
        {
            // 服务已停止并释放了启动信号，忽略。
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && !_stopRequested)
            {
                var message = wParam.ToInt32();
                var isDown = message is KeyboardNativeMethods.WM_KEYDOWN or KeyboardNativeMethods.WM_SYSKEYDOWN;
                var isUp = message is KeyboardNativeMethods.WM_KEYUP or KeyboardNativeMethods.WM_SYSKEYUP;
                if (isDown || isUp)
                {
                    var data = Marshal.PtrToStructure<KeyboardNativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    // 忽略其它程序（包括本程序上屏模拟）注入的按键，避免自己触发自己。
                    if ((data.flags & KeyboardNativeMethods.LLKHF_INJECTED) == 0)
                    {
                        var matcher = _matcher;
                        if (matcher is not null)
                        {
                            var events = isDown
                                ? matcher.ProcessKeyDown((int)data.vkCode)
                                : matcher.ProcessKeyUp((int)data.vkCode);
                            for (var i = 0; i < events.Count; i++)
                            {
                                RaiseHotkeyTriggered(events[i]);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("全局键盘钩子回调异常", ex);
        }

        // 永远继续传递按键，不能吞键。
        return KeyboardNativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>
    /// 低级鼠标钩子回调：把鼠标侧键/中键/左右键映射为虚拟键码交给同一个 <see cref="HotkeyMatcher"/>。
    /// 只有命中已配置热键的鼠标事件会被吞掉（返回 1），其余事件照常传递。
    /// </summary>
    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var swallow = false;
        try
        {
            if (nCode >= 0 && !_stopRequested)
            {
                var message = wParam.ToInt32();
                var isDown = message is KeyboardNativeMethods.WM_LBUTTONDOWN
                    or KeyboardNativeMethods.WM_RBUTTONDOWN
                    or KeyboardNativeMethods.WM_MBUTTONDOWN
                    or KeyboardNativeMethods.WM_XBUTTONDOWN;
                var isUp = message is KeyboardNativeMethods.WM_LBUTTONUP
                    or KeyboardNativeMethods.WM_RBUTTONUP
                    or KeyboardNativeMethods.WM_MBUTTONUP
                    or KeyboardNativeMethods.WM_XBUTTONUP;
                if (isDown || isUp)
                {
                    var data = Marshal.PtrToStructure<KeyboardNativeMethods.MSLLHOOKSTRUCT>(lParam);
                    // 忽略其它程序注入的鼠标事件（如自动化工具），避免误触发热键。
                    if ((data.flags & KeyboardNativeMethods.LLMHF_INJECTED) == 0
                        && TryMapMouseButton(message, data.mouseData, out var virtualKey))
                    {
                        var matcher = _matcher;
                        if (matcher is not null)
                        {
                            var events = isDown
                                ? matcher.ProcessKeyDown(virtualKey)
                                : matcher.ProcessKeyUp(virtualKey);
                            for (var i = 0; i < events.Count; i++)
                            {
                                RaiseHotkeyTriggered(events[i]);
                            }

                            // 命中已配置热键时吞掉该鼠标事件，避免浏览器前进/后退等系统默认行为同时触发。
                            swallow = events.Count > 0;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("全局鼠标钩子回调异常", ex);
        }

        return swallow
            ? new IntPtr(1)
            : KeyboardNativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static bool TryMapMouseButton(int message, uint mouseData, out int virtualKey)
    {
        switch (message)
        {
            case KeyboardNativeMethods.WM_LBUTTONDOWN:
            case KeyboardNativeMethods.WM_LBUTTONUP:
                virtualKey = VirtualKeys.MouseLeft;
                return true;
            case KeyboardNativeMethods.WM_RBUTTONDOWN:
            case KeyboardNativeMethods.WM_RBUTTONUP:
                virtualKey = VirtualKeys.MouseRight;
                return true;
            case KeyboardNativeMethods.WM_MBUTTONDOWN:
            case KeyboardNativeMethods.WM_MBUTTONUP:
                virtualKey = VirtualKeys.MouseMiddle;
                return true;
            case KeyboardNativeMethods.WM_XBUTTONDOWN:
            case KeyboardNativeMethods.WM_XBUTTONUP:
                // mouseData 高 16 位：1 = XBUTTON1、2 = XBUTTON2
                var xButton = (int)((mouseData >> 16) & 0xFFFF);
                virtualKey = xButton == 2 ? VirtualKeys.MouseX2 : VirtualKeys.MouseX1;
                return true;
            default:
                virtualKey = 0;
                return false;
        }
    }

    private static bool MatcherUsesMouse(HotkeyMatcher? matcher) =>
        matcher is not null
        && (matcher.PushToTalkGesture?.IsMouseButton == true
            || matcher.ToggleGesture?.IsMouseButton == true
            || matcher.CancelGesture?.IsMouseButton == true);

    private void RaiseHotkeyTriggered(HotkeyEventArgs args)
    {
        var handler = HotkeyTriggered;
        if (handler is null)
        {
            return;
        }

        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<HotkeyEventArgs>)target)(this, args);
            }
            catch (Exception ex)
            {
                _logger.Error("热键事件处理器抛出异常", ex);
            }
        }
    }

    private void ReportError(string message)
    {
        _logger.Error(message);
        var handler = Error;
        if (handler is null)
        {
            return;
        }

        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<string>)target)(this, message);
            }
            catch (Exception ex)
            {
                _logger.Error("热键错误事件处理器抛出异常", ex);
            }
        }
    }
}
