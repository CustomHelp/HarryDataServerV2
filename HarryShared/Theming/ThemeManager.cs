using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace HarryShared.Theming
{
    public enum AppTheme { Dark, Light }

    /// <summary>
    /// Runtime light/dark theme switch shared by all Harry apps. On each switch it REPLACES the
    /// palette <see cref="SolidColorBrush"/> resources with fresh brushes — the originals are
    /// frozen once consumed by sealed implicit-style setters, so they cannot be mutated in place
    /// (a <c>brush.Color = …</c> is silently skipped on a frozen Freezable). Every consumer
    /// references the palette via <c>DynamicResource</c>, so swapping the resource entries updates
    /// the UI live without reloading any window. The choice is persisted to
    /// <c>%LOCALAPPDATA%\HarrySuite\theme.txt</c> and is therefore shared across the whole suite.
    /// </summary>
    public static class ThemeManager
    {
        public static AppTheme Current { get; private set; } = AppTheme.Dark;

        private static readonly string StatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HarrySuite", "theme.txt");

        // Resource key -> (dark ARGB, light ARGB). Keys absent in a given app are skipped.
        // Accent/AccentLight and the semantic LED colours stay constant across themes.
        private static readonly (string Key, string Dark, string Light)[] Palette =
        {
            ("WindowBg",     "#FF1A1D23", "#FFEDEFF2"),
            ("PanelBg",      "#FF232730", "#FFFFFFFF"),
            ("CardBg",       "#FF2A2F3A", "#FFF4F6F9"),
            ("Accent",       "#FF6B21A8", "#FF6B21A8"),
            ("AccentLight",  "#FF8B5CF6", "#FF8B5CF6"),
            ("BorderBrush",  "#FF3A3F4B", "#FFCBD2DC"),
            ("TextBrush",    "#FFE5E7EB", "#FF1A1D23"),
            ("TextDimBrush", "#FF9CA3AF", "#FF64748B"),
            ("PopupBg",      "#FF22262F", "#FFFFFFFF"),
            ("HoverBg",      "#FF334155", "#FFE2E8F0"),
            ("ItemText",     "#FFE2E8F0", "#FF1A1D23"),
        };

        /// <summary>Apply the persisted theme (Dark if none saved). Call once at window startup.</summary>
        public static void Initialize()
        {
            var theme = AppTheme.Dark;
            try
            {
                if (File.Exists(StatePath) &&
                    File.ReadAllText(StatePath).Trim().Equals("Light", StringComparison.OrdinalIgnoreCase))
                {
                    theme = AppTheme.Light;
                }
            }
            catch { /* fall back to Dark */ }
            Apply(theme, persist: false);
        }

        public static void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

        public static void Apply(AppTheme theme, bool persist = true)
        {
            var app = Application.Current;
            if (app != null)
            {
                foreach (var (key, dark, light) in Palette)
                {
                    // Only touch palette keys this app actually defines (TryFindResource also
                    // searches the merged DarkTheme dictionary).
                    if (app.TryFindResource(key) is not SolidColorBrush)
                        continue;

                    var color = (Color)ColorConverter.ConvertFromString(theme == AppTheme.Dark ? dark : light);

                    // Replace the resource with a fresh brush rather than mutating the existing one:
                    // the original is frozen once used in a sealed style setter, so an in-place
                    // colour change is silently dropped. Setting the app-level resource shadows the
                    // merged-dictionary entry; DynamicResource consumers re-resolve to the new brush.
                    app.Resources[key] = new SolidColorBrush(color);
                }
            }
            Current = theme;
            if (persist)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                    File.WriteAllText(StatePath, theme.ToString());
                }
                catch { /* persistence is best-effort */ }
            }
        }
    }
}
