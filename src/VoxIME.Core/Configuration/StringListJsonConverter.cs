using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoxIME.Core.Configuration;

/// <summary>
/// 让同一个配置字段既能读旧版的单个字符串（如 "listeningMotionGroup": "TapBody"），
/// 也能读新版的字符串数组（如 ["Idle", "TapBody"]）。写回时统一写成数组，
/// 用于“状态动作映射”从单选升级为多选时的配置兼容。
/// </summary>
public sealed class StringListJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return new List<string>();
            case JsonTokenType.String:
            {
                var value = reader.GetString();
                return string.IsNullOrWhiteSpace(value) ? new List<string>() : new List<string> { value };
            }
            case JsonTokenType.StartArray:
            {
                var result = new List<string>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        break;
                    }

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var value = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            result.Add(value);
                        }
                    }
                }

                return result;
            }
            default:
                reader.Skip();
                return new List<string>();
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        if (value is not null)
        {
            foreach (var item in value)
            {
                writer.WriteStringValue(item);
            }
        }

        writer.WriteEndArray();
    }
}