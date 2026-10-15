using System.Windows;
using System.Windows.Media;

namespace FanControl.UI;

internal static class ChartColors
{
    public static readonly Brush Temp = Frozen(Color.FromRgb(0xFF, 0x8A, 0x3D));
    public static readonly Brush[] Fans =
    [
        Frozen(Color.FromRgb(0x3D, 0xB8, 0xFF)),
        Frozen(Color.FromRgb(0xA7, 0x8B, 0xFA)),
        Frozen(Color.FromRgb(0x34, 0xD3, 0x99)),
        Frozen(Color.FromRgb(0xF4, 0x72, 0xB6)),
    ];

    public static Brush Fan(int index) => Fans[index % Fans.Length];

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Theme brush from WPF-UI's resources, with a fallback before the theme loads.</summary>
    public static Brush Resource(FrameworkElement element, string key, Color fallback) =>
        element.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    public static Brush WithOpacity(Brush brush, double opacity)
    {
        var clone = brush.CloneCurrentValue();
        clone.Opacity = opacity;
        clone.Freeze();
        return clone;
    }
}
