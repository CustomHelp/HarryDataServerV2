using System.Windows.Media;

namespace HarryShared.Theming
{
    /// <summary>
    /// Frozen status-LED brushes for the companion tools, matching the semantic LED colours
    /// defined in <c>HarryShared\Themes\DarkTheme.xaml</c> (LedGreen/LedRed/LedGray). These stay
    /// constant across light/dark (the ThemeManager never swaps them), so a plain frozen brush per
    /// colour is enough — a view model can expose one directly as a <see cref="Brush"/> property and
    /// bind an <c>Ellipse.Fill</c> to it (same rendering pattern as the server's LED ellipses).
    /// </summary>
    public static class LedBrushes
    {
        public static readonly Brush Green = Freeze("#FF22C55E");
        public static readonly Brush Red = Freeze("#FFEF4444");
        public static readonly Brush Gray = Freeze("#FF6B7280");

        private static Brush Freeze(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
