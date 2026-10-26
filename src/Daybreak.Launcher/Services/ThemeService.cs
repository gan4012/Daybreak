using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Daybreak.Launcher.Services;

/// <summary>主题与背景效果的统一应用入口,配置值只认 LauncherConfig 里的字符串约定。</summary>
public static class ThemeService
{
    public static WindowBackdropType GetBackdropType(string? backdrop) => backdrop switch
    {
        "Mica" => WindowBackdropType.Mica,
        "Tabbed" => WindowBackdropType.Tabbed,
        "None" => WindowBackdropType.None,
        _ => WindowBackdropType.Acrylic,
    };

    public static void ApplyThemeMode(string? mode, string? backdrop = null)
    {
        var backdropType = GetBackdropType(backdrop);
        switch (mode)
        {
            case "Light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light, backdropType);
                break;
            case "System":
                SystemThemeManager.UpdateSystemThemeCache();
                // 4.3 的系统主题缓存返回 SystemTheme 枚举,只映射亮暗两档
                var systemTheme = SystemThemeManager.GetCachedSystemTheme();
                ApplicationThemeManager.Apply(
                    systemTheme == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
                    backdropType);
                break;
            default: // Dark
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, backdropType);
                break;
        }
    }

    public static void ApplyBackdrop(Window window, string? backdrop)
    {
        WindowBackdrop.ApplyBackdrop(window, GetBackdropType(backdrop));
    }
}
