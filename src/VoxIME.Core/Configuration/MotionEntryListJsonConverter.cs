using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoxIME.Core.Configuration;

/// <summary>
/// 动作组条目列表的 JSON 转换器：
/// 兼容旧配置的字符串数组（"A" / ["A","B"]），也支持带模式的对象（[{"group":"A","loop":false}]）。
/// 写入时统一用对象数组；自动模式（Loop 为 null）省略 loop 字段。
/// </summary>
public sealed class MotionEntryListJsonConverter : JsonConverter<List<Live2DMotionEntry>>
{
    public override List<Live2DMotionEntry> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var result = new List<Live2DMotionEntry>();
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return result;
            case JsonTokenType.String:
                AddString(result, reader.GetString());
                return result;
            case JsonTokenType.StartObject:
                AddEntry(result, ReadEntry(ref reader));
                return result;
            case JsonTokenType.StartArray:
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        break;
                    }

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        AddString(result, reader.GetString());
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        AddEntry(result, ReadEntry(ref reader));
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                return result;
            default:
                reader.Skip();
                return result;
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<Live2DMotionEntry> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        if (value is not null)
        {
            foreach (var entry in value)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Group))
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("group", entry.Group);
                if (entry.Loop.HasValue)
                {
                    writer.WriteBoolean("loop", entry.Loop.Value);
                }

                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
    }

    private static void AddString(List<Live2DMotionEntry> result, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            result.Add(new Live2DMotionEntry(value.Trim()));
        }
    }

    private static void AddEntry(List<Live2DMotionEntry> result, Live2DMotionEntry? entry)
    {
        if (entry is not null && !string.IsNullOrWhiteSpace(entry.Group))
        {
            result.Add(entry);
        }
    }

    private static Live2DMotionEntry? ReadEntry(ref Utf8JsonReader reader)
    {
        string? group = null;
        bool? loop = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                break;
            }

            if (string.Equals(propertyName, "group", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "name", StringComparison.OrdinalIgnoreCase))
            {
                group = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (string.Equals(propertyName, "loop", StringComparison.OrdinalIgnoreCase))
            {
                loop = reader.TokenType switch
                {
                    JsonTokenType.True => true,
                    JsonTokenType.False => false,
                    _ => null
                };
            }
            else
            {
                reader.Skip();
            }
        }

        return string.IsNullOrWhiteSpace(group)
            ? null
            : new Live2DMotionEntry { Group = group.Trim(), Loop = loop };
    }
}
