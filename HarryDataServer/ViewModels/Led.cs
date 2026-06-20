using System.Windows.Media;

namespace HarryDataServer.ViewModels;

/// <summary>Shared frozen LED/status brushes used across the view models.</summary>
internal static class Led
{
    public static readonly Brush Green = Freeze(Color.FromRgb(0x22, 0xC5, 0x5E));
    public static readonly Brush Orange = Freeze(Color.FromRgb(0xF5, 0x9E, 0x0B));
    public static readonly Brush Red = Freeze(Color.FromRgb(0xEF, 0x44, 0x44));
    public static readonly Brush Gray = Freeze(Color.FromRgb(0x6B, 0x72, 0x80));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
