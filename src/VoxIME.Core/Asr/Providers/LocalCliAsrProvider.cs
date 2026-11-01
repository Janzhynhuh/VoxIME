using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Asr.Providers;

/// <summary>
/// 本地命令行 ASR 识别器：把音频写成临时 WAV，按参数模板启动本机程序，再按
/// <see cref="ProviderProfile.OutputMode"/>（stdout / file / json）取识别结果。
/// </summary>
/// <remarks>
/// 参数模板占位符：<c>{wav}</c>（临时 WAV 绝对路径）、<c>{out}</c>（去掉扩展名后的输出路径前缀）、
/// <c>{lang}</c>（语言代码）、<c>{model}</c>（profile.Model，便于 whisper.cpp 指定模型路径）。
/// 超时使用 profile.TimeoutSeconds；超时/取消会杀掉整个进程树并清理临时文件。
/// stderr 会截取前 1000 字符放入异常信息，便于用户排查命令参数问题。
/// </remarks>
public sealed class LocalCliAsrProvider : IAsrProvider
{
    private const string ModeStdout = "stdout";
    private const string ModeFile = "file";
    private const string ModeJson = "json";

    private readonly ProviderProfile _profile;
    private readonly IAppLogger _logger;
    private readonly string _command;
    private readonly IReadOnlyList<string> _argumentTemplate;
    private readonly string _outputMode;
    private readonly string _outputFileTemplate;

    public LocalCliAsrProvider(ProviderProfile profile, HttpClient httpClient, IAppLogger logger)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentNullException.ThrowIfNull(httpClient);
        _logger = logger ?? NullLogger.Instance;

        if (string.IsNullOrWhiteSpace(profile.Command))
        {
            throw new AsrException(
                "本地 CLI 识别器缺少可执行文件（Command，示例：whisper-cli 或 C:\\tools\\whisper-cli.exe）。",
                AsrProviderTypeIds.LocalCli);
        }

        _command = profile.Command.Trim();
        _argumentTemplate = CliArgumentParser.Split(profile.CommandArguments);
        _outputMode = NormalizeOutputMode(profile.OutputMode);
        _outputFileTemplate = string.IsNullOrWhiteSpace(profile.OutputFileTemplate)
            ? "{out}.txt"
            : profile.OutputFileTemplate.Trim();
    }

    /// <inheritdoc />
    public AsrProviderDescriptor Descriptor { get; } =
        AsrProviderCatalog.GetDescriptor(AsrProviderTypeIds.LocalCli)
        ?? throw new InvalidOperationException("内置 ASR 目录缺少 local-cli 描述。");

    /// <summary>
    /// 临时文件根目录覆盖（默认 %TEMP%\VoxIME\asr-cli）。测试用：可指向自定义目录并断言已被清理。
    /// </summary>
    public string? TempDirectoryOverride { get; set; }

    /// <inheritdoc />
    public async Task<AsrResult> TranscribeAsync(
        AudioClip clip,
        AsrCallOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        cancellationToken.ThrowIfCancellationRequested();

        options ??= new AsrCallOptions();
        var language = AsrHttpSupport.ResolveLanguage(options.Language, _profile.Language);
        var extra = AsrHttpSupport.MergeExtraJson(_profile.ExtraJson, options.ExtraJson, Descriptor.TypeId);
        var wav = clip.ToWav();

        var tempRoot = string.IsNullOrWhiteSpace(TempDirectoryOverride)
            ? Path.Combine(Path.GetTempPath(), "VoxIME", "asr-cli")
            : TempDirectoryOverride!.Trim();
        var workDirectory = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        var wavPath = Path.Combine(workDirectory, "audio.wav");
        var outputBasePath = Path.Combine(workDirectory, "audio");

        _logger.Info(
            $"ASR[{Descriptor.DisplayName}] 启动本地 CLI：{_command}，语言 {language}，" +
            $"模式 {_outputMode}，音频 {clip.Duration.TotalSeconds:F1} 秒。");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(workDirectory);
            await File.WriteAllBytesAsync(wavPath, wav, cancellationToken).ConfigureAwait(false);

            var arguments = BuildArguments(wavPath, outputBasePath, language, _profile.Model);
            var (stdout, stderr) = await RunProcessAsync(arguments, cancellationToken).ConfigureAwait(false);
            var text = ExtractText(stdout, stderr, wavPath, outputBasePath, language, extra);
            stopwatch.Stop();

            _logger.Debug(
                $"ASR[{Descriptor.DisplayName}] 本地 CLI 完成，退出码 0，耗时 {stopwatch.ElapsedMilliseconds} ms，文本长度 {text.Length}。");

            return new AsrResult(text, Descriptor.TypeId, stopwatch.Elapsed, clip.Duration);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>把参数模板中的占位符替换为实际值（public 便于单测）。</summary>
    public IReadOnlyList<string> BuildArguments(string wavPath, string outputBasePath, string language, string? model)
    {
        var arguments = new List<string>(_argumentTemplate.Count);
        foreach (var token in _argumentTemplate)
        {
            arguments.Add(ReplacePlaceholders(token, wavPath, outputBasePath, language, model));
        }

        return arguments;
    }

    /// <summary>替换单个参数中的占位符（public 便于单测）。</summary>
    public static string ReplacePlaceholders(
        string? token,
        string wavPath,
        string outputBasePath,
        string language,
        string? model)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "";
        }

        return token
            .Replace("{wav}", wavPath, StringComparison.Ordinal)
            .Replace("{out}", outputBasePath, StringComparison.Ordinal)
            .Replace("{lang}", language, StringComparison.Ordinal)
            .Replace("{model}", model ?? "", StringComparison.Ordinal);
    }

    /// <summary>按点号路径取 JSON 节点（支持数组下标，如 result.0.text）；public 便于单测。</summary>
    public static JsonNode? ResolveJsonPath(JsonNode? root, string? path)
    {
        if (root is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return root;
        }

        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current is JsonArray array && int.TryParse(segment, out var index))
            {
                if (index < 0 || index >= array.Count)
                {
                    return null;
                }

                current = array[index];
                continue;
            }

            if (current is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(segment, out var child))
                {
                    return null;
                }

                current = child;
                continue;
            }

            return null;
        }

        return current;
    }

    private static string NormalizeOutputMode(string? outputMode)
    {
        var mode = string.IsNullOrWhiteSpace(outputMode) ? ModeStdout : outputMode.Trim().ToLowerInvariant();
        return mode switch
        {
            ModeStdout or ModeFile or ModeJson => mode,
            _ => throw new AsrException(
                $"本地 CLI 识别器的 OutputMode 只能是 stdout / file / json，当前值：“{outputMode}”。",
                AsrProviderTypeIds.LocalCli)
        };
    }

    private string ExtractText(
        string stdout,
        string stderr,
        string wavPath,
        string outputBasePath,
        string language,
        JsonObject extra)
    {
        switch (_outputMode)
        {
            case ModeStdout:
                return RequireText(stdout.Trim(), stderr, "标准输出（stdout）");

            case ModeFile:
            {
                var outputPath = ResolveOutputFilePath(wavPath, outputBasePath, language);
                if (!File.Exists(outputPath))
                {
                    throw new AsrException(
                        $"本地识别程序没有生成输出文件“{outputPath}”。请检查参数模板与 OutputFileTemplate。{StderrSuffix(stderr)}",
                        Descriptor.TypeId);
                }

                return RequireText(File.ReadAllText(outputPath).Trim(), stderr, $"输出文件“{outputPath}”");
            }

            case ModeJson:
            {
                var raw = stdout.Trim();
                if (raw.Length == 0)
                {
                    // 部分 CLI 会把 JSON 结果写入输出文件，stdout 为空时兜底读取输出文件。
                    var fallbackPath = ResolveOutputFilePath(wavPath, outputBasePath, language);
                    if (File.Exists(fallbackPath))
                    {
                        raw = File.ReadAllText(fallbackPath).Trim();
                    }
                }

                if (raw.Length == 0)
                {
                    throw new AsrException(
                        $"本地识别程序的 JSON 输出为空。{StderrSuffix(stderr)}",
                        Descriptor.TypeId);
                }

                JsonNode? root;
                try
                {
                    root = JsonNode.Parse(raw);
                }
                catch (JsonException ex)
                {
                    throw new AsrException(
                        $"无法解析本地识别程序输出的 JSON：{ex.Message}{StderrSuffix(stderr)}",
                        Descriptor.TypeId,
                        responseBody: AsrHttpSupport.Truncate(raw),
                        innerException: ex);
                }

                var textPath = AsrHttpSupport.GetString(extra["textPath"]);
                if (string.IsNullOrWhiteSpace(textPath))
                {
                    textPath = "text";
                }

                var value = ResolveJsonPath(root, textPath);
                var text = AsrHttpSupport.GetString(value);
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new AsrException(
                        $"本地识别程序输出的 JSON 中找不到文本路径“{textPath}”（可用 ExtraJson 的 textPath 指定）。",
                        Descriptor.TypeId,
                        responseBody: AsrHttpSupport.Truncate(raw));
                }

                return text.Trim();
            }

            default:
                throw new AsrException($"不支持的 OutputMode：“{_outputMode}”。", Descriptor.TypeId);
        }
    }

    private string ResolveOutputFilePath(string wavPath, string outputBasePath, string language) =>
        ReplacePlaceholders(_outputFileTemplate, wavPath, outputBasePath, language, _profile.Model);

    private static string RequireText(string text, string stderr, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AsrException(
                $"本地识别程序没有输出识别结果（来源：{source}）。{StderrSuffix(stderr)}",
                AsrProviderTypeIds.LocalCli);
        }

        return text;
    }

    private static string StderrSuffix(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "";
        }

        return $" 错误输出（stderr）：{AsrHttpSupport.Truncate(stderr, 1000)}";
    }

    private async Task<(string Stdout, string Stderr)> RunProcessAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _command,
            // 保持应用当前目录，便于 CLI 使用相对模型路径；临时 WAV 始终传绝对路径。
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new AsrException(
                    $"无法启动本地识别程序“{_command}”（Process.Start 返回 false）。",
                    Descriptor.TypeId);
            }
        }
        catch (Win32Exception ex)
        {
            throw new AsrException(
                $"无法启动本地识别程序“{_command}”：{ex.Message}。请确认 Command 是绝对路径或已在 PATH 中。",
                Descriptor.TypeId,
                innerException: ex);
        }

        _logger.Debug($"本地 CLI 参数：{_command} {string.Join(' ', arguments)}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = AsrHttpSupport.CreateTimeoutSource(_profile.TimeoutSeconds, cancellationToken);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            KillProcessTree(process);
            await DrainQuietlyAsync(stdoutTask, stderrTask).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Info($"ASR[{Descriptor.DisplayName}] 本地 CLI 已被调用方取消。");
                throw;
            }

            _logger.Warn($"ASR[{Descriptor.DisplayName}] 本地 CLI 超时（{_profile.TimeoutSeconds} 秒），已终止进程树。");
            throw new AsrException(
                $"本地识别程序超时（{_profile.TimeoutSeconds} 秒），已终止。可调大该服务的超时时间或检查模型加载是否过慢。",
                Descriptor.TypeId,
                innerException: ex);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            _logger.Warn($"ASR[{Descriptor.DisplayName}] 本地 CLI 退出码 {process.ExitCode}。");
            throw new AsrException(
                $"本地识别程序退出码 {process.ExitCode}。{StderrSuffix(stderr)}",
                Descriptor.TypeId);
        }

        return (stdout, stderr);
    }

    private static async Task DrainQuietlyAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 读取管道失败不影响取消/超时的主流程。
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
            // 进程可能已经自行退出，忽略。
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
