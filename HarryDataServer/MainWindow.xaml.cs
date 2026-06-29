using System.Windows;
using HarryDataServer.ViewModels;
using HarryDataServer.Theming;

namespace HarryDataServer;

/// <summary>
/// Main application window. A thin shell over <see cref="MainViewModel"/>: the
/// dashboard tabs bind to the view model, which mirrors every subsystem's live state
/// (CLAUDE.md section 14).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ThemeManager.Initialize();
        UpdateThemeButton();
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeButton();
    }

    private void UpdateThemeButton() =>
        ThemeToggle.Content = ThemeManager.Current == AppTheme.Dark ? "☀ Light" : "🌙 Dark";
}
