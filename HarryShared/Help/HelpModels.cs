namespace HarryShared.Help;

/// <summary>The two languages the shared help window can show (toggled at runtime, persisted suite-wide).</summary>
public enum HelpLang { En, De }

/// <summary>One keyboard shortcut row — the key combo plus its action in both languages.</summary>
public sealed record HelpShortcut(string Keys, string ActionEn, string ActionDe);

/// <summary>One step/paragraph of a how-to section, in both languages.</summary>
public sealed record HelpStep(string En, string De);

/// <summary>A titled how-to section (e.g. "Teach a part") with an ordered list of steps.</summary>
public sealed record HelpSection(string TitleEn, string TitleDe, IReadOnlyList<HelpStep> Steps);

/// <summary>
/// The full help content for ONE app, in English + German. Rendered by the shared
/// <c>HelpWindow</c>; supplied per app by <see cref="SuiteHelp"/>. Language is toggled in the window.
/// </summary>
public sealed record HelpContent(
    string AppName,
    string Version,
    string DescriptionEn,
    string DescriptionDe,
    IReadOnlyList<HelpSection> Sections,
    IReadOnlyList<HelpShortcut> Shortcuts,
    string SupportEn = "Support: CustomHelp — customhelp.de",
    string SupportDe = "Support: CustomHelp — customhelp.de");
