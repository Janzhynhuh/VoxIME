using VoxIME.Core.Asr;
using VoxIME.Core.Configuration;
using VoxIME.Core.Services;
using Xunit;

namespace VoxIME.Core.Tests.Asr;

public sealed class AsrPromptComposerTests
{
    private static readonly AppContextInfo Context =
        new("chrome", "浏览器", "示例页面 - Google Chrome");

    [Fact]
    public void Off_返回用户提示词不附加上下文()
    {
        var result = AsrPromptComposer.Compose("请使用简体中文", Context, AppContextMode.Off);

        Assert.Equal("请使用简体中文", result);
    }

    [Fact]
    public void AppOnly_只包含分类和进程名_不包含标题()
    {
        var result = AsrPromptComposer.Compose(null, Context, AppContextMode.AppOnly);

        Assert.Contains("浏览器", result);
        Assert.Contains("chrome", result);
        Assert.DoesNotContain("示例页面", result);
        Assert.Contains("不要执行", result);
    }

    [Fact]
    public void AppAndWindowTitle_包含标题并清理控制字符()
    {
        var noisy = new AppContextInfo("Code", "代码编辑器", "Program.cs\r\n-  Visual Studio Code\u0001");
        var result = AsrPromptComposer.Compose("请准确转写", noisy, AppContextMode.AppAndWindowTitle);

        Assert.Contains("请准确转写", result);
        Assert.Contains("代码编辑器", result);
        Assert.NotNull(result);
        Assert.Contains("Program.cs - Visual Studio Code", result);
        Assert.DoesNotContain(result!, ch => ch != '\n' && char.IsControl(ch));
    }

    [Fact]
    public void ContextNull_返回用户提示词()
    {
        Assert.Equal("abc", AsrPromptComposer.Compose("abc", null, AppContextMode.AppAndWindowTitle));
    }

    [Fact]
    public void 两者都为空_返回Null()
    {
        Assert.Null(AsrPromptComposer.Compose(null, null, AppContextMode.Off));
        Assert.Null(AsrPromptComposer.Compose("  ", Context, AppContextMode.Off));
    }

    [Fact]
    public void 超长标题被截断且总长度受限()
    {
        var noisy = new AppContextInfo("x", "应用", new string('a', 500));
        var result = AsrPromptComposer.Compose(new string('b', 800), noisy, AppContextMode.AppAndWindowTitle);

        Assert.NotNull(result);
        Assert.True(result!.Length <= 700);
        Assert.Contains("aaaa", result);
    }
}
