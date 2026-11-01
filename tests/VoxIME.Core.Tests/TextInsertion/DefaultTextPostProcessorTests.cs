using VoxIME.Core.Configuration;
using VoxIME.Core.TextInsertion;

namespace VoxIME.Core.Tests.TextInsertion;

/// <summary>识别结果后处理测试。</summary>
public class DefaultTextPostProcessorTests
{
    private readonly DefaultTextPostProcessor _processor = new();

    [Fact]
    public void TrimWhitespace控制首尾空白()
    {
        var trim = new InsertionConfig { TrimWhitespace = true };
        var keep = new InsertionConfig { TrimWhitespace = false };

        Assert.Equal("你好", _processor.Process("  你好 \r\n", trim));
        Assert.Equal("  你好 \n", _processor.Process("  你好 \r\n", keep));
    }

    [Fact]
    public void 术语替换长串优先()
    {
        var config = new InsertionConfig
        {
            Replacements =
            {
                new TextReplacement { From = "语音输入", To = "语音" },
                new TextReplacement { From = "语音输入法", To = "VoxIME" }
            }
        };

        Assert.Equal("VoxIME", _processor.Process("语音输入法", config));
        Assert.Equal("语音", _processor.Process("语音输入", config));
    }

    [Fact]
    public void 术语替换支持删除()
    {
        var config = new InsertionConfig
        {
            Replacements = { new TextReplacement { From = "嗯", To = "" } }
        };

        Assert.Equal("你好啊", _processor.Process("嗯你好嗯啊", config));
    }

    [Fact]
    public void AppendSpace追加单个尾部空格()
    {
        var config = new InsertionConfig { AppendSpace = true };

        Assert.Equal("你好 ", _processor.Process("你好", config));
        Assert.Equal("你好 ", _processor.Process("你好   ", config));
        Assert.Equal(string.Empty, _processor.Process("", config));
        Assert.Equal(string.Empty, _processor.Process("   ", config));
    }

    [Fact]
    public void 移除控制字符但保留换行与制表符()
    {
        var config = new InsertionConfig();

        Assert.Equal("你好世界", _processor.Process("你\u0000好\u0007世\u001F界", config));
        Assert.Equal("a\tb", _processor.Process("a\tb", config));
        Assert.Equal("第一行\n第二行", _processor.Process("第一行\n第二行", config));
    }

    [Fact]
    public void 统一换行风格为LF()
    {
        var config = new InsertionConfig();

        Assert.Equal("a\nb\nc", _processor.Process("a\r\nb\rc", config));
    }

    [Fact]
    public void 移除零宽字符和BOM()
    {
        var config = new InsertionConfig();

        Assert.Equal("你好世界", _processor.Process("\uFEFF\u200B你好\u200E世界\u2060", config));
    }

    [Fact]
    public void 空输入返回空字符串()
    {
        var config = new InsertionConfig();

        Assert.Equal(string.Empty, _processor.Process(null!, config));
        Assert.Equal(string.Empty, _processor.Process(string.Empty, config));
        Assert.Equal(string.Empty, _processor.Process("   ", config));
    }

    [Fact]
    public void 未启用可选处理时保留原文内部空格()
    {
        var config = new InsertionConfig { TrimWhitespace = false, AppendSpace = false };

        Assert.Equal("你 好  世界", _processor.Process("你 好  世界", config));
        Assert.Equal("你 好  世界 ", _processor.Process("你 好  世界 ", config));
    }
}
