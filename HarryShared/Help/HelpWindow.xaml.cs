using System.Windows;
using HarryShared.Theming;

namespace HarryShared.Help;

/// <summary>
/// The shared, themed help window used by every Harry app. Open it with an app-specific
/// <see cref="HelpContent"/>; the EN/DE toggle and theme are shared across the whole suite.
/// </summary>
public partial class HelpWindow : Window
{
    public HelpWindow(HelpContent content)
    {
        // Ensure the persisted theme is applied even if this window is opened very early.
        ThemeManager.Initialize();
        InitializeComponent();
        DataContext = new HelpViewModel(content, HelpLanguage.Load());
    }

    /// <summary>Convenience: show the help modally, owned by the given window.</summary>
    public static void Show(Window? owner, HelpContent content)
    {
        var win = new HelpWindow(content) { Owner = owner };
        win.ShowDialog();
    }
}
