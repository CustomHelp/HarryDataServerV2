using System.IO;

namespace HarryShared.Help;

/// <summary>
/// Suite-wide persisted choice of help language, stored next to the theme in
/// <c>%LOCALAPPDATA%\HarrySuite\language.txt</c> (mirrors <see cref="Theming.ThemeManager"/>).
/// Default is English when nothing is saved.
/// </summary>
public static class HelpLanguage
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HarrySuite", "language.txt");

    /// <summary>The last chosen language (English if none saved / unreadable).</summary>
    public static HelpLang Load()
    {
        try
        {
            if (File.Exists(StatePath) &&
                File.ReadAllText(StatePath).Trim().Equals("De", StringComparison.OrdinalIgnoreCase))
                return HelpLang.De;
        }
        catch { /* fall back to English */ }
        return HelpLang.En;
    }

    /// <summary>Persist the chosen language (best-effort).</summary>
    public static void Save(HelpLang lang)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, lang.ToString());
        }
        catch { /* persistence is best-effort */ }
    }
}
