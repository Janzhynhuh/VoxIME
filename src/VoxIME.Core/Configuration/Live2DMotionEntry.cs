using System.Text.Json.Serialization;

namespace VoxIME.Core.Configuration;

/// <summary>
/// 一个状态 / 待机动作组的配置条目：组名 + 播放模式。
/// <see cref="Loop"/> 为 null 表示“自动”：组名含 idle 时循环，否则播一次并保持末帧。
/// </summary>
public sealed class Live2DMotionEntry
{
    public Live2DMotionEntry()
    {
    }

    public Live2DMotionEntry(string group, bool? loop = null)
    {
        Group = group;
        Loop = loop;
    }

    public string Group { get; set; } = "";

    /// <summary>true = 循环播放；false = 播一次并保持末帧；null = 自动按名字判断。</summary>
    public bool? Loop { get; set; }

    /// <summary>实际生效的循环标记（null 时按组名猜）。</summary>
    [JsonIgnore]
    public bool EffectiveLoop => Loop ?? DefaultLoopFor(Group);

    /// <summary>默认规则：组名含 idle（忽略大小写）→ 循环，否则单次保持。</summary>
    public static bool DefaultLoopFor(string? group) =>
        (group ?? "").IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0;
}
