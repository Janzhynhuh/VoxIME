using System.Text;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Asr.Providers;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;
using Xunit;

namespace VoxIME.Core.Tests.Asr;

public sealed class LocalCliAsrProviderTests
{
    private static readonly HttpClient UnusedHttpClient = new(new FakeHttpMessageHandler(_ =>
        throw new InvalidOperationException("本地 CLI 不应发起 HTTP 请求。")));

    private static LocalCliAsrProvider Create(ProviderProfile profile, string tempRoot, FakeAppLogger? logger = null)
    {
        Directory.CreateDirectory(tempRoot);
        return new LocalCliAsrProvider(profile, UnusedHttpClient, logger ?? new FakeAppLogger())
        {
            TempDirectoryOverride = tempRoot
        };
    }

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "voxime-asr-tests", Guid.NewGuid().ToString("N"));

    private static void AssertTempCleaned(string tempRoot)
    {
        Assert.True(Directory.Exists(tempRoot), "临时根目录应保留（只有子工作目录会被清理）。");
        Assert.Empty(Directory.GetDirectories(tempRoot));
    }

    [Fact]
    public void 参数模板解析_支持引号与空白()
    {
        Assert.Equal(new[] { "-m", "models/ggml-base.bin", "-f", "a.wav", "-l", "zh" },
            CliArgumentParser.Split("-m models/ggml-base.bin -f a.wav -l zh"));
        Assert.Equal(new[] { "-c", "echo hello world" },
            CliArgumentParser.Split("-c \"echo hello world\""));
        Assert.Equal(new[] { "--text", "", "末尾" },
            CliArgumentParser.Split("--text \"\" 末尾"));
        Assert.Equal(new[] { "a\"b" }, CliArgumentParser.Split("\"a\\\"b\""));
        Assert.Empty(CliArgumentParser.Split("   "));
    }

    [Fact]
    public void 占位符替换_支持wav_out_lang_model()
    {
        var provider = Create(
            AsrTestData.ProcessProfile("cmd", "-f {wav} -of {out} -l {lang} -m {model}"),
            NewTempRoot());

        var arguments = provider.BuildArguments(@"C:\tmp\a.wav", @"C:\tmp\a", "zh", "ggml-base.bin");

        Assert.Equal(new[] { "-f", @"C:\tmp\a.wav", "-of", @"C:\tmp\a", "-l", "zh", "-m", "ggml-base.bin" }, arguments);
    }

    [Fact]
    public async Task stdout模式_取标准输出并清理临时目录()
    {
        var tempRoot = NewTempRoot();
        var provider = Create(AsrTestData.ProcessProfile("cmd", "/c echo hello-cli-001"), tempRoot);

        var result = await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.Equal("hello-cli-001", result.Text);
        Assert.Equal(AsrProviderTypeIds.LocalCli, result.ProviderId);
        AssertTempCleaned(tempRoot);
    }

    [Fact]
    public async Task file模式_读取OutputFileTemplate指定的文件()
    {
        var tempRoot = NewTempRoot();
        var provider = Create(
            AsrTestData.ProcessProfile("cmd", "/c echo file-mode-ok>{out}.txt", outputMode: "file", outputFileTemplate: "{out}.txt"),
            tempRoot);

        var result = await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.Equal("file-mode-ok", result.Text);
        AssertTempCleaned(tempRoot);
    }

    [Fact]
    public async Task json模式_按ExtraJson的textPath取文本()
    {
        var tempRoot = NewTempRoot();
        var jsonPath = Path.Combine(Path.GetTempPath(), "voxime-asr-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(jsonPath, "{\"result\":{\"text\":\"json-cli-ok\"}}", Encoding.UTF8);
        try
        {
            var provider = Create(
                AsrTestData.ProcessProfile(
                    "cmd",
                    $"/c type {jsonPath}",
                    outputMode: "json",
                    extraJson: "{\"textPath\":\"result.text\"}"),
                tempRoot);

            var result = await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

            Assert.Equal("json-cli-ok", result.Text);
            AssertTempCleaned(tempRoot);
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }

    [Fact]
    public async Task json模式_路径不存在时_抛出中文异常()
    {
        var tempRoot = NewTempRoot();
        var jsonPath = Path.Combine(Path.GetTempPath(), "voxime-asr-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(jsonPath, "{\"foo\":1}", Encoding.UTF8);
        try
        {
            var provider = Create(
                AsrTestData.ProcessProfile(
                    "cmd",
                    $"/c type {jsonPath}",
                    outputMode: "json",
                    extraJson: "{\"textPath\":\"result.text\"}"),
                tempRoot);

            var exception = await Assert.ThrowsAsync<AsrException>(
                () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

            Assert.Contains("result.text", exception.Message);
            AssertTempCleaned(tempRoot);
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }

    [Fact]
    public async Task file模式_未生成文件时_抛出中文异常()
    {
        var tempRoot = NewTempRoot();
        var provider = Create(
            AsrTestData.ProcessProfile("cmd", "/c exit 0", outputMode: "file", outputFileTemplate: "{out}.txt"),
            tempRoot);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

        Assert.Contains("没有生成输出文件", exception.Message);
        AssertTempCleaned(tempRoot);
    }

    [Fact]
    public async Task stdout为空时_抛出中文异常()
    {
        var tempRoot = NewTempRoot();
        var provider = Create(AsrTestData.ProcessProfile("cmd", "/c exit 0"), tempRoot);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

        Assert.Contains("没有输出识别结果", exception.Message);
        AssertTempCleaned(tempRoot);
    }

    [Fact]
    public async Task 非零退出码_异常包含退出码与stderr()
    {
        var tempRoot = NewTempRoot();
        var provider = Create(AsrTestData.ProcessProfile("cmd", "/c exit 3"), tempRoot);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

        Assert.Contains("退出码 3", exception.Message);
        AssertTempCleaned(tempRoot);
    }

    [Fact]
    public async Task 超时_终止进程并抛出AsrException()
    {
        var tempRoot = NewTempRoot();
        var provider = Create(
            AsrTestData.ProcessProfile("cmd", "/c ping -n 30 127.0.0.1", timeoutSeconds: 1),
            tempRoot);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));
        stopwatch.Stop();

        Assert.Contains("超时", exception.Message);
        Assert.True(stopwatch.ElapsedMilliseconds < 10000, $"超时终止过慢：{stopwatch.ElapsedMilliseconds}ms");
        AssertTempCleaned(tempRoot);
    }

    [Fact]
    public async Task 调用方取消_抛出OperationCanceledException并清理()
    {
        var tempRoot = NewTempRoot();
        var provider = Create(AsrTestData.ProcessProfile("cmd", "/c ping -n 30 127.0.0.1"), tempRoot);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions(), cts.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 10000, $"取消终止过慢：{stopwatch.ElapsedMilliseconds}ms");
        AssertTempCleaned(tempRoot);
    }

    [Fact]
    public void 缺少Command或非法OutputMode时_构造抛中文异常()
    {
        var missingCommand = Assert.Throws<AsrException>(() =>
            new LocalCliAsrProvider(AsrTestData.ProcessProfile("", "-f {wav}"), UnusedHttpClient, NullLogger.Instance));
        Assert.Contains("Command", missingCommand.Message);

        var invalidMode = Assert.Throws<AsrException>(() =>
            new LocalCliAsrProvider(
                AsrTestData.ProcessProfile("cmd", "/c echo x", outputMode: "xml"),
                UnusedHttpClient,
                NullLogger.Instance));
        Assert.Contains("OutputMode", invalidMode.Message);
    }

    [Fact]
    public async Task 可执行文件不存在时_抛出中文启动异常()
    {
        var tempRoot = NewTempRoot();
        var provider = Create(
            AsrTestData.ProcessProfile("voxime-not-existing-cli-xyz", "-f {wav}"),
            tempRoot);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));
        Assert.Contains("无法启动本地识别程序", exception.Message);
    }
}
