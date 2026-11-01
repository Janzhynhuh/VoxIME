using System.Text;
using System.Text.Json;

namespace VoxIME.Core.Logging;

public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error
}

public interface IAppLogger
{
    string LogFilePath { get; }
    void Log(LogLevel level, string message, Exception? exception = null);
    void Trace(string message);
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

public static class AppLoggerExtensions
{
    public static void InfoFormat(this IAppLogger logger, string format, params object?[] args) =>
        logger.Info(string.Format(format, args));
}

/// <summary>轻量文件日志：按大小滚动，线程安全。</summary>
public sealed class FileLogger : IAppLogger, IDisposable
{
    private readonly object _sync = new();
    private readonly LogLevel _minLevel;
    private readonly long _maxBytes;
    private readonly int _retainFiles;
    private readonly string _directory;
    private DateTime _lastRetentionCheck = DateTime.MinValue;

    public FileLogger(
        string? directory = null,
        LogLevel minLevel = LogLevel.Info,
        long maxBytes = 2 * 1024 * 1024,
        int retainFiles = 5)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? AppInfo.DefaultLogDirectory : directory!;
        _minLevel = minLevel;
        _maxBytes = maxBytes;
        _retainFiles = Math.Max(1, retainFiles);
        LogFilePath = Path.Combine(_directory, $"voxime-{DateTime.Now:yyyyMMdd}.log");
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception)
        {
            // 日志目录不可写时退化为静默，不影响主流程。
        }
    }

    public string LogFilePath { get; private set; }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minLevel)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
            .Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        line.AppendLine();
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                if (File.Exists(LogFilePath) && new FileInfo(LogFilePath).Length > _maxBytes)
                {
                    RollFile();
                }

                File.AppendAllText(LogFilePath, line.ToString(), Encoding.UTF8);
                TryCleanupOldFiles();
            }
            catch (Exception)
            {
                // 忽略日志失败。
            }
        }
    }

    private void RollFile()
    {
        var index = 1;
        string target;
        do
        {
            target = Path.Combine(_directory, $"voxime-{DateTime.Now:yyyyMMdd}.{index}.log");
            index++;
        } while (File.Exists(target) && index < 100);

        try
        {
            File.Move(LogFilePath, target, overwrite: true);
        }
        catch (Exception)
        {
            // 忽略。
        }
    }

    private void TryCleanupOldFiles()
    {
        if (DateTime.UtcNow - _lastRetentionCheck < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastRetentionCheck = DateTime.UtcNow;
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var file in Directory.EnumerateFiles(_directory, "voxime-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // 忽略。
        }
    }

    public void Trace(string message) => Log(LogLevel.Trace, message);
    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warn(string message) => Log(LogLevel.Warn, message);
    public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);

    public void Dispose()
    {
    }

    public static LogLevel ParseLevel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info
    };
}

/// <summary>不输出任何内容的日志实现（测试/默认值）。</summary>
public sealed class NullLogger : IAppLogger
{
    public static readonly NullLogger Instance = new();

    public string LogFilePath => "";
    public void Log(LogLevel level, string message, Exception? exception = null) { }
    public void Trace(string message) { }
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
