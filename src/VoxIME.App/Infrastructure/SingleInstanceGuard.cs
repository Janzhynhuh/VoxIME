using System.Diagnostics;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 单实例守卫：命名互斥体 + 命名事件。
/// 第二个实例通过 SignalExistingInstance 通知首个实例（用于 --settings 直接唤起设置）。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\VoxIME.SingleInstance.v1";
    private const string ActivateEventName = @"Local\VoxIME.ActivateSettings.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private Thread? _listenerThread;
    private volatile bool _disposed;

    public bool IsFirstInstance { get; private set; }

    /// <summary>另一个实例请求打开设置（在后台线程触发，订阅方需自行封送到 UI 线程）。</summary>
    public event EventHandler? ActivateSettingsRequested;

    /// <summary>尝试成为首个实例。返回 false 时调用方应提示并退出。</summary>
    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            IsFirstInstance = createdNew;
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName, out _);
            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "VoxIME.SingleInstanceListener"
            };
            _listenerThread.Start();
            return true;
        }
        catch (Exception ex)
        {
            // 互斥体创建失败（极端权限/命名冲突）时宁可允许运行，避免应用完全无法启动。
            Debug.WriteLine($"[SingleInstanceGuard] {ex.Message}");
            IsFirstInstance = true;
            return true;
        }
    }

    /// <summary>由第二个实例调用：通知已运行的实例打开设置窗口。</summary>
    public static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SingleInstanceGuard] 通知已运行实例失败：{ex.Message}");
        }
    }

    private void ListenLoop()
    {
        while (!_disposed)
        {
            try
            {
                if (_activateEvent is not null && _activateEvent.WaitOne(500))
                {
                    if (!_disposed)
                    {
                        ActivateSettingsRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SingleInstanceGuard] 监听失败：{ex.Message}");
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _activateEvent?.Set();
        }
        catch (Exception)
        {
            // 忽略。
        }

        _listenerThread?.Join(300);
        _activateEvent?.Dispose();
        _activateEvent = null;

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 非拥有线程/已被释放。
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }
}
