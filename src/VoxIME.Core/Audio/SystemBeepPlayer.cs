using NAudio.Wave;
using VoxIME.Core.Abstractions;

namespace VoxIME.Core.Audio;

/// <summary>
/// 基于 NAudio 的提示音播放器：在后台线程合成并播放约 80ms 的短音。
/// </summary>
/// <remarks>
/// 完全非阻塞：<see cref="Play"/> 立即返回，播放失败（例如没有声卡、设备被独占）会被静默吞掉，
/// 不影响录音主流程。同一时刻只播放一个提示音，快速连发时后来的会被跳过。
/// </remarks>
public sealed class SystemBeepPlayer : IBeepPlayer
{
    private int _playing;

    public SystemBeepPlayer()
    {
    }

    public void Play(AudioCue cue)
    {
        // 用 Interlocked 做"正在播放则跳过"，避免 UI 连点堆积大量线程池任务。
        if (Interlocked.CompareExchange(ref _playing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                PlayCore(cue);
            }
            catch (Exception)
            {
                // 提示音属于锦上添花，任何异常都不能影响主流程。
            }
            finally
            {
                Volatile.Write(ref _playing, 0);
            }
        });
    }

    private static void PlayCore(AudioCue cue)
    {
        var wav = BeepToneFactory.CreateWav(cue);
        using var stream = new MemoryStream(wav, writable: false);
        using var reader = new WaveFileReader(stream);
        using var output = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 2
        };
        output.Init(reader);
        output.Play();

        // 等待播放结束（加一点余量），最长不超过 2 秒，避免异常卡死线程池线程。
        var timeout = BeepToneFactory.TotalDurationMs(cue) + 1500;
        var waited = 0;
        while (output.PlaybackState == PlaybackState.Playing && waited < timeout)
        {
            Thread.Sleep(5);
            waited += 5;
        }

        try
        {
            output.Stop();
        }
        catch (Exception)
        {
            // 忽略。
        }
    }
}
