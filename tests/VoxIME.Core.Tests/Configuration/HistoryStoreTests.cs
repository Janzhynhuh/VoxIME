using VoxIME.Core.Abstractions;
using VoxIME.Core.History;

namespace VoxIME.Core.Tests.Configuration;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public HistoryStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voxime-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "history.jsonl");
    }

    private static TranscriptionRecord Record(string text, DateTimeOffset? at = null) => new()
    {
        Text = text,
        ProviderName = "测试服务",
        ProviderTypeId = "mock",
        CreatedAt = at ?? DateTimeOffset.Now,
        AudioDuration = TimeSpan.FromSeconds(2),
        Latency = TimeSpan.FromMilliseconds(300)
    };

    [Fact]
    public void AddAndLoad_ReturnsNewestFirst()
    {
        var store = new JsonlHistoryStore(_path);

        store.Add(Record("第一条", DateTimeOffset.Now.AddSeconds(-3)));
        store.Add(Record("第二条", DateTimeOffset.Now.AddSeconds(-2)));
        store.Add(Record("第三条", DateTimeOffset.Now.AddSeconds(-1)));

        var loaded = store.Load();

        Assert.Equal(3, loaded.Count);
        Assert.Equal("第三条", loaded[0].Text);
        Assert.Equal("第一条", loaded[2].Text);
    }

    [Fact]
    public void Load_WithLimit_ReturnsSubset()
    {
        var store = new JsonlHistoryStore(_path);
        store.Add(Record("a"));
        store.Add(Record("b"));
        store.Add(Record("c"));

        var loaded = store.Load(limit: 2);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("c", loaded[0].Text);
        Assert.Equal("b", loaded[1].Text);
    }

    [Fact]
    public void Load_SkipsCorruptLines()
    {
        var store = new JsonlHistoryStore(_path);
        store.Add(Record("正常"));
        File.AppendAllText(_path, "这不是 json" + Environment.NewLine);

        var loaded = store.Load();

        Assert.Single(loaded);
        Assert.Equal("正常", loaded[0].Text);
    }

    [Fact]
    public void Clear_RemovesAllRecords()
    {
        var store = new JsonlHistoryStore(_path);
        store.Add(Record("记录"));

        store.Clear();

        Assert.Empty(store.Load());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Add_RaisesChangedEvent()
    {
        var store = new JsonlHistoryStore(_path);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Add(Record("事件"));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Record_FormatsDurationAndTime()
    {
        var record = Record("格式", DateTimeOffset.Now);
        Assert.Contains("2.0s", record.DurationLabel);
        Assert.Matches(@"\d{2}:\d{2}:\d{2}", record.LocalTimeLabel);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
