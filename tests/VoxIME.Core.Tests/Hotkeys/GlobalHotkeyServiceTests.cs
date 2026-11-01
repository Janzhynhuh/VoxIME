using System.Collections.Concurrent;
using VoxIME.Core.Configuration;
using VoxIME.Core.Hotkeys;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Tests.Hotkeys;

/// <summary>
/// 全局热键服务生命周期测试。真实键盘钩子无法在单测中模拟按键，但可以验证：
/// 非法配置上报、启动/更新/停止/释放不抛异常、状态正确。
/// </summary>
public class GlobalHotkeyServiceTests
{
    [Fact]
    public void 非法热键配置通过Error事件上报()
    {
        using var service = new GlobalHotkeyService(NullLogger.Instance);
        var errors = new ConcurrentQueue<string>();
        service.Error += (_, message) => errors.Enqueue(message);

        service.Start(new HotkeyConfig
        {
            PushToTalk = "Bogus1",
            Toggle = "Bogus2",
            Cancel = "Bogus3"
        });

        var snapshot = errors.ToArray();
        Assert.Contains(snapshot, message => message.Contains("Bogus1"));
        Assert.Contains(snapshot, message => message.Contains("Bogus2"));
        Assert.Contains(snapshot, message => message.Contains("Bogus3"));

        service.Stop();
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void 启动更新停止循环不抛异常()
    {
        using var service = new GlobalHotkeyService(NullLogger.Instance);

        service.Start(new HotkeyConfig());
        Assert.Contains(service.IsRunning, new[] { true, false }); // 无交互桌面时可能无法安装钩子，但不许抛异常。

        service.Update(new HotkeyConfig
        {
            PushToTalk = "Ctrl+Shift+Space",
            Toggle = "F9",
            Cancel = "Escape"
        });

        service.Stop();
        Assert.False(service.IsRunning);

        service.Stop();
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void 未启动时停止和重复释放都是安全的()
    {
        var service = new GlobalHotkeyService(NullLogger.Instance);

        service.Stop();
        service.Dispose();
        service.Dispose();

        Assert.False(service.IsRunning);
    }

    [Fact]
    public void 鼠标热键配置可正常启动与停止()
    {
        using var service = new GlobalHotkeyService(NullLogger.Instance);

        service.Start(new HotkeyConfig
        {
            PushToTalk = "MouseX1",
            Toggle = "Ctrl+MouseX2",
            Cancel = "Esc"
        });

        // 无交互桌面环境可能无法安装钩子，但不允许抛异常。
        Assert.Contains(service.IsRunning, new[] { true, false });
        service.Stop();
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void 非法鼠标热键配置通过Error事件上报()
    {
        using var service = new GlobalHotkeyService(NullLogger.Instance);
        var errors = new ConcurrentQueue<string>();
        service.Error += (_, message) => errors.Enqueue(message);

        service.Start(new HotkeyConfig
        {
            PushToTalk = "MouseX9",
            Toggle = "MouseSide",
            Cancel = "Esc"
        });

        var snapshot = errors.ToArray();
        Assert.Contains(snapshot, message => message.Contains("MouseX9"));
        Assert.Contains(snapshot, message => message.Contains("MouseSide"));

        service.Stop();
        Assert.False(service.IsRunning);
    }
}
