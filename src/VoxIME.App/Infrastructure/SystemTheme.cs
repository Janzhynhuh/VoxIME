using Microsoft.Win32;

namespace VoxIME.App.Infrastructure;

/// <summary>读取 Windows 个性化设置里的「应用模式」（浅色/深色）。</summary>
internal static class SystemTheme
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>
    /// 系统应用模式是否为浅色。
    /// 读不到（旧系统/策略限制/非交互会话）时按深色处理，与本应用默认主题一致。
    /// </summary>
    public static bool IsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            if (key?.GetValue(AppsUseLightThemeValue) is int value)
            {
                return value != 0;
            }
        }
        catch (Exception)
        {
            // 注册表不可读时静默回退。
        }

        return false;
    }
}
