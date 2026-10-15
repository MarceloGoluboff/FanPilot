using System.Windows;
using FanControl.Settings;
using Wpf.Ui.Appearance;

namespace FanControl.UI;

internal static class ThemeService
{
    static bool _watching;

    public static void Apply(AppTheme theme, Window window)
    {
        // WPF-UI throws when unwatching a window that hasn't loaded, so only undo a watch we started.
        if (_watching && window.IsLoaded)
        {
            SystemThemeWatcher.UnWatch(window);
            _watching = false;
        }

        switch (theme)
        {
            case AppTheme.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
            case AppTheme.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                if (!_watching)
                {
                    SystemThemeWatcher.Watch(window);
                    _watching = true;
                }
                break;
        }
        // Owner-drawn charts pick up the new brushes on their next render.
        window.InvalidateVisual();
    }
}
