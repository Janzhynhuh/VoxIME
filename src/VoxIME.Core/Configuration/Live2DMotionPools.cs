using System.Text.Json.Serialization;

namespace VoxIME.Core.Configuration;

/// <summary>待机随机池条目：每隔一段时间从池中按权重随机播放一次。</summary>
public sealed class Live2DPoolEntry
{
    public Live2DPoolEntry()
    {
    }

    public Live2DPoolEntry(string group, int weight = 1, bool hold = false)
    {
        Group = group;
        Weight = weight;
        Hold = hold;
    }

    public string Group { get; set; } = "";

    /// <summary>随机权重（1..10，越大越容易被抽中）。</summary>
    public int Weight { get; set; } = 1;

    /// <summary>true = 播完保持末帧；false = 播完回到静默循环姿势（默认）。</summary>
    public bool Hold { get; set; }
}

/// <summary>触发式单次动作：由点击桌宠 / 模型加载等事件触发。</summary>
public sealed class Live2DTriggerEntry
{
    public Live2DTriggerEntry()
    {
    }

    public Live2DTriggerEntry(string group, string triggerEvent, bool hold = false, int weight = 1)
    {
        Group = group;
        Event = triggerEvent;
        Hold = hold;
        Weight = weight;
    }

    public string Group { get; set; } = "";

    /// <summary>触发事件：start（模型加载后）或 tap（点击桌宠 / 托盘「互动一下」）。</summary>
    public string Event { get; set; } = Live2DTriggerEvents.Tap;

    /// <summary>true = 播完保持末帧；false = 播完回到待机（默认）。</summary>
    public bool Hold { get; set; }

    /// <summary>同一事件有多个候选时的随机权重（1..10）。</summary>
    public int Weight { get; set; } = 1;
}

/// <summary>触发事件常量。</summary>
public static class Live2DTriggerEvents
{
    public const string Tap = "tap";
    public const string Start = "start";

    /// <summary>归一化事件名；未知值回退 tap。</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Tap;
        }

        return string.Equals(value.Trim(), Start, StringComparison.OrdinalIgnoreCase) ? Start : Tap;
    }
}
