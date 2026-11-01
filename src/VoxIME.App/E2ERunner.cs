using System.IO;
using System.Text.Json;
using NAudio.Wave;
using VoxIME.Core;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;

namespace VoxIME.App;

/// <summary>
/// 端到端自检模式（隐藏开关 <c>--e2e</c>）：
/// 真实启动全部服务 → 等待指定秒数（便于外部把焦点切到目标输入框）→ 录音 3 秒 →
/// 走完整识别/后处理/上屏/历史/朗读/桌宠流程 → 把结果写入 %TEMP%\voxime-e2e-result.json 后退出。
/// 若麦克风没有采集到有效语音，则用合成音频走一次识别+上屏，保证无声环境也能验证链路。
/// 环境变量：VOXIME_E2E_DELAY_SECONDS（默认 6）、VOXIME_E2E_RECORD_SECONDS（默认 3）。
/// </summary>
internal static class E2ERunner
{
    public static async Task<int> RunAsync(AppHost host)
    {
        var logger = host.Logger;
        var delaySeconds = ReadIntEnvironment("VOXIME_E2E_DELAY_SECONDS", 6, 1, 60);
        var recordSeconds = ReadIntEnvironment("VOXIME_E2E_RECORD_SECONDS", 3, 1, 15);
        var fallbackUsed = false;
        TranscriptionRecord? record = null;
        string? error = null;
        var inserted = false;
        var exitCode = 2;

        try
        {
            logger.Info($"[E2E] 启动完成，{delaySeconds}s 后开始录音（{recordSeconds}s），请把焦点切到目标输入框。");
            await WaitForLive2DAsync(host, logger, TimeSpan.FromSeconds(8)).ConfigureAwait(true);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(true);

            var profile = host.Config.GetActiveProfile();
            logger.Info($"[E2E] 使用服务：{profile?.Name ?? "(无)"}（{profile?.TypeId ?? "-"}）");

            // 设置了 VOXIME_E2E_FORCE_AUDIO_WAV=1 时跳过真实录音，直接用指定 WAV 走识别+上屏，
            // 便于在无声卡/无人说话的 CI 或远程环境验证真实厂商接口。
            var forcedWav = Environment.GetEnvironmentVariable("VOXIME_E2E_AUDIO_WAV");
            var forceAudioWav = string.Equals(
                                     Environment.GetEnvironmentVariable("VOXIME_E2E_FORCE_AUDIO_WAV"),
                                     "1",
                                     StringComparison.Ordinal)
                                 && !string.IsNullOrWhiteSpace(forcedWav)
                                 && File.Exists(forcedWav);
            if (forceAudioWav)
            {
                logger.Info($"[E2E] 强制使用指定 WAV（跳过真实录音）：{forcedWav}");
                record = null;
            }
            else
            {
                var started = host.Controller.StartRecording();
                logger.Info($"[E2E] StartRecording={started}");
                await Task.Delay(TimeSpan.FromSeconds(recordSeconds)).ConfigureAwait(true);

                try
                {
                    record = await host.Controller.StopAndTranscribeAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    logger.Error("[E2E] 真实录音识别失败", ex);
                }
            }

            if (record is null || string.IsNullOrWhiteSpace(record.Text) || !record.Inserted)
            {
                fallbackUsed = true;
                logger.Warn("[E2E] 真实录音未得到可用文本，改用合成/指定音频验证识别+上屏链路。");
                if (profile is null)
                {
                    error ??= "没有可用的语音识别服务";
                }
                else
                {
                    try
                    {
                        var clip = LoadFallbackClip(logger);
                        var result = await host.Controller.TranscribeAsync(clip, profile).ConfigureAwait(true);
                        var insertion = await host.Controller.InsertTextAsync(result.Text).ConfigureAwait(true);
                        inserted = insertion.Success;
                        record = new TranscriptionRecord
                        {
                            Text = result.Text,
                            ProviderName = profile.Name,
                            ProviderTypeId = profile.TypeId,
                            AudioDuration = result.AudioDuration,
                            Latency = result.Latency,
                            Inserted = insertion.Success
                        };
                        error = insertion.Success ? null : insertion.Error;
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        logger.Error("[E2E] 合成音频识别/上屏失败", ex);
                    }
                }
            }
            else
            {
                inserted = record.Inserted;
            }

            if (record is not null)
            {
                inserted = record.Inserted;
            }

            await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(true);
            exitCode = inserted && record is not null && !string.IsNullOrWhiteSpace(record.Text) ? 0 : 2;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.Error("[E2E] 未处理异常", ex);
            exitCode = 3;
        }
        finally
        {
            var payload = new
            {
                success = exitCode == 0,
                exitCode,
                fallbackUsed,
                inserted,
                text = record?.Text,
                provider = record?.ProviderName,
                providerTypeId = record?.ProviderTypeId,
                audioDurationSeconds = record?.AudioDuration.TotalSeconds,
                latencyMs = record?.Latency.TotalMilliseconds,
                appState = host.Controller.State.ToString(),
                error,
                configPath = host.ConfigStore.ConfigFilePath,
                historyPath = host.History.FilePath,
                timestamp = DateTimeOffset.Now
            };

            try
            {
                var path = Path.Combine(Path.GetTempPath(), "voxime-e2e-result.json");
                File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                logger.Info($"[E2E] 结果已写入 {path}：success={payload.success}, inserted={inserted}, text={record?.Text ?? "(空)"}");
            }
            catch (Exception ex)
            {
                logger.Warn($"[E2E] 写入结果文件失败：{ex.Message}");
            }
        }

        return exitCode;
    }

    /// <summary>
    /// 兜底音频：优先读取环境变量 VOXIME_E2E_AUDIO_WAV 指定的 WAV（用于真实语音文件联调），
    /// 否则使用 2 秒 440Hz 合成音（仅验证识别/上屏链路是否打通）。
    /// </summary>
    private static AudioClip LoadFallbackClip(VoxIME.Core.Logging.IAppLogger logger)
    {
        var wavPath = Environment.GetEnvironmentVariable("VOXIME_E2E_AUDIO_WAV");
        if (!string.IsNullOrWhiteSpace(wavPath) && File.Exists(wavPath))
        {
            try
            {
                var clip = LoadWavAsClip(wavPath);
                logger.Info($"[E2E] 使用指定 WAV 音频：{wavPath}（{clip.Duration.TotalSeconds:0.0}s, {clip.SampleRate}Hz）");
                return clip;
            }
            catch (Exception ex)
            {
                logger.Warn($"[E2E] 读取指定 WAV 失败，退回合成音频：{ex.Message}");
            }
        }

        logger.Info("[E2E] 使用 440Hz 合成音频（仅验证识别链路）。");
        return AudioClip.CreateTone(TimeSpan.FromSeconds(2), 440);
    }

    /// <summary>读取任意 PCM WAV，必要时重采样为 16kHz 单声道 PCM16。</summary>
    private static AudioClip LoadWavAsClip(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        IWaveProvider provider = reader;
        IDisposable? resampler = null;
        try
        {
            var needsConvert = reader.WaveFormat.SampleRate != 16000
                               || reader.WaveFormat.Channels != 1
                               || reader.WaveFormat.BitsPerSample != 16
                               || reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm;
            if (needsConvert)
            {
                var converter = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1))
                {
                    ResamplerQuality = 60
                };
                resampler = converter;
                provider = converter;
            }

            using var ms = new MemoryStream();
            var buffer = new byte[provider.WaveFormat.BlockAlign * 1024];
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                ms.Write(buffer, 0, read);
            }

            return AudioClip.FromPcm16(ms.ToArray(), 16000, 1);
        }
        finally
        {
            resampler?.Dispose();
        }
    }

    private static async Task WaitForLive2DAsync(AppHost host, VoxIME.Core.Logging.IAppLogger logger, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (host.Live2D.IsAvailable)
            {
                logger.Info("[E2E] Live2D 已就绪");
                return;
            }

            await Task.Delay(250).ConfigureAwait(true);
        }

        logger.Warn("[E2E] 等待 Live2D 就绪超时，继续执行语音链路。");
    }

    private static int ReadIntEnvironment(string name, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) ? Math.Clamp(value, min, max) : fallback;
    }
}
