using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;
using VoxIME.Core.TextInsertion;

namespace VoxIME.Core.Tests.TextInsertion;

/// <summary>文本上屏逻辑测试：剪贴板与键盘模拟全部用伪造实现，不碰真实系统状态。</summary>
public class ClipboardTextInjectorTests
{
    [Fact]
    public async Task 剪贴板粘贴模式会还原原剪贴板()
    {
        var (injector, clipboard, input) = CreateInjector();
        clipboard.Current = "用户原来的内容";

        var result = await injector.InsertAsync("你好世界", new InsertionConfig());

        Assert.True(result.Success);
        Assert.Equal(4, result.CharacterCount);
        Assert.False(result.CopiedToClipboard);
        Assert.Equal("用户原来的内容", clipboard.Current);
        Assert.Equal(new[] { "你好世界", "用户原来的内容" }, clipboard.SetHistory);
        Assert.Contains("paste", input.Actions);
    }

    [Fact]
    public async Task 关闭还原后会保留文本在剪贴板()
    {
        var (injector, clipboard, input) = CreateInjector();
        clipboard.Current = "旧内容";
        var config = new InsertionConfig { RestoreClipboard = false };

        var result = await injector.InsertAsync("新的识别结果", config);

        Assert.True(result.Success);
        Assert.True(result.CopiedToClipboard);
        Assert.Equal("新的识别结果", clipboard.Current);
        Assert.Contains("paste", input.Actions);
    }

    [Fact]
    public async Task 原剪贴板不是文本时无法还原但不会报错()
    {
        var (injector, clipboard, _) = CreateInjector();
        clipboard.ReturnNull = true;

        var result = await injector.InsertAsync("纯文本", new InsertionConfig());

        Assert.True(result.Success);
        Assert.True(result.CopiedToClipboard);
        Assert.Equal("纯文本", clipboard.Current);
    }

    [Fact]
    public async Task 粘贴后可按配置追加回车()
    {
        var (injector, _, input) = CreateInjector();
        var config = new InsertionConfig { PressEnterAfterInsert = true };

        var result = await injector.InsertAsync("请发送", config);

        Assert.True(result.Success);
        Assert.Equal(new[] { "paste", "enter" }, input.Actions);
    }

    [Fact]
    public async Task 剪贴板被占用时返回失败()
    {
        var (injector, clipboard, input) = CreateInjector();
        clipboard.ThrowOnSet = true;

        var result = await injector.InsertAsync("文本", new InsertionConfig());

        Assert.False(result.Success);
        Assert.False(result.CopiedToClipboard);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.DoesNotContain("paste", input.Actions);
    }

    [Fact]
    public async Task 发送粘贴快捷键失败时文本留在剪贴板()
    {
        var (injector, clipboard, input) = CreateInjector();
        input.PasteResult = false;

        var result = await injector.InsertAsync("兜底文本", new InsertionConfig());

        Assert.False(result.Success);
        Assert.True(result.CopiedToClipboard);
        Assert.Equal("兜底文本", clipboard.Current);
    }

    [Fact]
    public async Task 剪贴板模式支持多行文本()
    {
        var (injector, clipboard, input) = CreateInjector();
        var text = "第一行\n第二行";

        var result = await injector.InsertAsync(text, new InsertionConfig());

        Assert.True(result.Success);
        Assert.Equal(text, clipboard.SetHistory[0]);
        Assert.DoesNotContain("enter", input.Actions);
    }

    [Fact]
    public async Task 逐字模拟输入支持中文与代理对()
    {
        var (injector, clipboard, input) = CreateInjector();
        const string text = "你好😀";
        var config = new InsertionConfig { Mode = InsertionMode.SimulateTyping };

        var result = await injector.InsertAsync(text, config);

        Assert.True(result.Success);
        Assert.Equal(text, string.Concat(input.TypedElements));
        Assert.All(input.TypedElements, element => Assert.False(HasLoneSurrogate(element)));
        Assert.Equal(0, clipboard.SetCallCount);
        Assert.DoesNotContain("paste", input.Actions);
    }

    [Fact]
    public async Task 逐字模拟输入把换行转换为回车键()
    {
        var (injector, _, input) = CreateInjector();
        var config = new InsertionConfig { Mode = InsertionMode.SimulateTyping };

        var result = await injector.InsertAsync("a\nb", config);

        Assert.True(result.Success);
        Assert.Equal(new[] { "type:a", "enter", "type:b" }, input.Actions);

        // 回车不算字符数，但 CharacterCount 以原文长度计。
        Assert.Equal(3, result.CharacterCount);
    }

    [Fact]
    public async Task 逐字模拟输入失败时用剪贴板兜底()
    {
        var (injector, clipboard, input) = CreateInjector();
        input.FailTypingAtCall = 1;
        var config = new InsertionConfig { Mode = InsertionMode.SimulateTyping };

        var result = await injector.InsertAsync("一二三", config);

        Assert.False(result.Success);
        Assert.True(result.CopiedToClipboard);
        Assert.Equal("一二三", clipboard.Current);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task 逐字模拟输入也支持追加回车()
    {
        var (injector, _, input) = CreateInjector();
        var config = new InsertionConfig { Mode = InsertionMode.SimulateTyping, PressEnterAfterInsert = true };

        var result = await injector.InsertAsync("你好", config);

        Assert.True(result.Success);
        Assert.Equal(new[] { "type:你", "type:好", "enter" }, input.Actions);
    }

    [Fact]
    public async Task 空文本直接返回成功且不做任何操作()
    {
        var (injector, clipboard, input) = CreateInjector();

        var result = await injector.InsertAsync(string.Empty, new InsertionConfig());

        Assert.True(result.Success);
        Assert.Equal(0, result.CharacterCount);
        Assert.Equal(0, clipboard.SetCallCount);
        Assert.Empty(input.Actions);
    }

    [Fact]
    public async Task 已取消的令牌会安全失败()
    {
        var (injector, _, input) = CreateInjector(static (delay, ct) => Task.Delay(delay, ct));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await injector.InsertAsync("不会插入", new InsertionConfig(), cts.Token);

        Assert.False(result.Success);
        Assert.Contains("取消", result.Error);
        Assert.DoesNotContain("paste", input.Actions);
    }

    private static (ClipboardTextInjector Injector, FakeClipboardService Clipboard, FakeInputSimulator Input) CreateInjector(
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var clipboard = new FakeClipboardService();
        var input = new FakeInputSimulator();
        var injector = new ClipboardTextInjector(
            clipboard,
            NullLogger.Instance,
            input,
            delay ?? (static (_, _) => Task.CompletedTask));
        return (injector, clipboard, input);
    }

    private static bool HasLoneSurrogate(string element)
    {
        for (var i = 0; i < element.Length; i++)
        {
            if (char.IsHighSurrogate(element[i]))
            {
                if (i + 1 >= element.Length || !char.IsLowSurrogate(element[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(element[i]))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public string? Current { get; set; }

        public bool ThrowOnSet { get; set; }

        public bool ReturnNull { get; set; }

        public List<string> SetHistory { get; } = new();

        public int SetCallCount => SetHistory.Count;

        public void SetText(string text)
        {
            if (ThrowOnSet)
            {
                throw new InvalidOperationException("剪贴板被其他程序占用");
            }

            SetHistory.Add(text);
            Current = text;
        }

        public string? TryGetText() => ReturnNull ? null : Current;
    }

    private sealed class FakeInputSimulator : IInputSimulator
    {
        private int _typingCalls;

        public List<string> Actions { get; } = new();

        public List<string> TypedElements { get; } = new();

        public bool PasteResult { get; set; } = true;

        public bool EnterResult { get; set; } = true;

        public int FailTypingAtCall { get; set; } = -1;

        public bool SendPasteShortcut()
        {
            Actions.Add("paste");
            return PasteResult;
        }

        public bool SendEnter()
        {
            Actions.Add("enter");
            return EnterResult;
        }

        public bool SendUnicodeChar(string element)
        {
            var call = _typingCalls++;
            if (FailTypingAtCall >= 0 && call == FailTypingAtCall)
            {
                Actions.Add("typing-failed");
                return false;
            }

            TypedElements.Add(element);
            Actions.Add("type:" + element);
            return true;
        }
    }
}
