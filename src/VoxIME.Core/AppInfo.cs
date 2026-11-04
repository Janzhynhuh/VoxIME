using System.IO;

namespace VoxIME.Core;

/// <summary>产品级常量与本地数据目录约定。所有组件都通过这里定位应用数据。</summary>
public static class AppInfo
{
    public const string ProductName = "VoxIME 声语";
    public const string EnglishName = "VoxIME";
    public const string Version = "0.4.1";
    public const string ConfigFileName = "config.json";
    public const string DefaultHotkeyPushToTalk = "Ctrl+Alt";
    public const string DefaultHotkeyToggle = "Ctrl+Alt+M";

    /// <summary>%APPDATA%\VoxIME</summary>
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), EnglishName);

    public static string DefaultConfigPath => Path.Combine(AppDataDirectory, ConfigFileName);

    public static string DefaultLogDirectory => Path.Combine(AppDataDirectory, "logs");

    public static string DefaultHistoryPath => Path.Combine(AppDataDirectory, "history.jsonl");

    public static string DefaultAudioCacheDirectory => Path.Combine(AppDataDirectory, "audio");

    public static string DefaultModelDirectory => Path.Combine(AppDataDirectory, "models");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(DefaultLogDirectory);
        Directory.CreateDirectory(DefaultAudioCacheDirectory);
        Directory.CreateDirectory(DefaultModelDirectory);
    }

    public static string GetVersionLabel() => $"{EnglishName} {Version}";
}
