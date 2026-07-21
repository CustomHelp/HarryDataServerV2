using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HarryShared.Help;

/// <summary>A section as shown for the current language (title + already-localised step lines).</summary>
public sealed record SectionView(string Title, IReadOnlyList<string> Steps);

/// <summary>A shortcut as shown for the current language (key combo + localised action).</summary>
public sealed record ShortcutView(string Keys, string Action);

/// <summary>
/// View model for the shared help window: holds the app's <see cref="HelpContent"/> and the current
/// language, and exposes every label/section/shortcut already resolved to that language. Toggling the
/// language re-notifies all bindings and persists the choice suite-wide.
/// </summary>
public sealed partial class HelpViewModel : ObservableObject
{
    private readonly HelpContent _content;

    public HelpViewModel(HelpContent content, HelpLang language)
    {
        _content = content;
        _language = language;
    }

    [ObservableProperty] private HelpLang _language;

    private bool En => Language == HelpLang.En;

    public string Title => $"{_content.AppName} — {(En ? "Help" : "Hilfe")}";
    public string AppName => _content.AppName;
    public string Version => _content.Version;
    public string Description => En ? _content.DescriptionEn : _content.DescriptionDe;

    public string HowToHeader => En ? "How to use" : "Bedienung";
    public string ShortcutsHeader => En ? "Keyboard shortcuts" : "Tastenkürzel";
    public string AboutHeader => En ? "About" : "Über";
    public string Support => En ? _content.SupportEn : _content.SupportDe;

    public string SwitchLabel => En ? "Deutsch" : "English";
    public string CloseLabel => En ? "Close" : "Schließen";
    public string VersionLabel => (En ? "Version " : "Version ") + _content.Version;

    public IReadOnlyList<SectionView> Sections => _content.Sections
        .Select(s => new SectionView(
            En ? s.TitleEn : s.TitleDe,
            s.Steps.Select(st => En ? st.En : st.De).ToList()))
        .ToList();

    public IReadOnlyList<ShortcutView> Shortcuts => _content.Shortcuts
        .Select(s => new ShortcutView(s.Keys, En ? s.ActionEn : s.ActionDe))
        .ToList();

    /// <summary>Switch EN↔DE, persist the choice, and refresh every language-dependent binding.</summary>
    [RelayCommand]
    private void ToggleLanguage()
    {
        Language = En ? HelpLang.De : HelpLang.En;
        HelpLanguage.Save(Language);
        OnPropertyChanged(string.Empty); // re-evaluate all computed properties for the new language
    }
}
