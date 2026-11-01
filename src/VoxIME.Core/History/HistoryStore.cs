using System.Text;
using System.Text.Json;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Core.History;

public interface IHistoryStore
{
    string FilePath { get; }
    event EventHandler? Changed;
    IReadOnlyList<TranscriptionRecord> Load(int? limit = null);
    void Add(TranscriptionRecord record);
    void Clear();
}

/// <summary>JSONL 历史记录存储（追加写，读取时跳过损坏行）。</summary>
public sealed class JsonlHistoryStore : IHistoryStore
{
    private readonly object _sync = new();
    private readonly IAppLogger _logger;
    private readonly int _maxRecords;

    public JsonlHistoryStore(string? filePath = null, IAppLogger? logger = null, int maxRecords = 1000)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? AppInfo.DefaultHistoryPath : filePath!;
        _logger = logger ?? NullLogger.Instance;
        _maxRecords = Math.Max(50, maxRecords);
    }

    public string FilePath { get; }
    public event EventHandler? Changed;

    public IReadOnlyList<TranscriptionRecord> Load(int? limit = null)
    {
        lock (_sync)
        {
            var records = new List<TranscriptionRecord>();
            if (!File.Exists(FilePath))
            {
                return records;
            }

            try
            {
                foreach (var line in File.ReadLines(FilePath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var record = JsonSerializer.Deserialize<TranscriptionRecord>(line, ConfigJson.Options);
                        if (record is not null)
                        {
                            records.Add(record);
                        }
                    }
                    catch (JsonException)
                    {
                        // 跳过损坏行。
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"读取历史记录失败：{ex.Message}");
            }

            records.Reverse();
            return limit is > 0 ? records.Take(limit.Value).ToList() : records;
        }
    }

    public void Add(TranscriptionRecord record)
    {
        lock (_sync)
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(FilePath, JsonSerializer.Serialize(record, ConfigJson.CompactOptions) + Environment.NewLine, Encoding.UTF8);
                TrimIfNeeded();
            }
            catch (Exception ex)
            {
                _logger.Warn($"写入历史记录失败：{ex.Message}");
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"清空历史记录失败：{ex.Message}");
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TrimIfNeeded()
    {
        try
        {
            var lines = File.ReadAllLines(FilePath, Encoding.UTF8);
            if (lines.Length <= _maxRecords)
            {
                return;
            }

            var keep = lines.Skip(lines.Length - _maxRecords);
            File.WriteAllLines(FilePath, keep, Encoding.UTF8);
        }
        catch (Exception)
        {
            // 忽略。
        }
    }
}
