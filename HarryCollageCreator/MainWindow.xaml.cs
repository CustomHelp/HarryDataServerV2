using System.Windows;
using HarryShared.Theming;

namespace HarryCollageCreator;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Initialize();
        UpdateThemeButton();
    }

    private void OnChangeConfig(object sender, RoutedEventArgs e)
    {
        if (HarryShared.Config.HarryConfig.ShowChangeDialog("HarryCollageCreator"))
            MessageBox.Show(this,
                "Config-Pfad gespeichert. Bitte das Werkzeug neu starten, damit die neue Harry.ini/Datenbank aktiv wird.",
                "Neustart nötig", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeButton();
    }

    private void UpdateThemeButton() =>
        ThemeToggle.Content = ThemeManager.Current == AppTheme.Dark ? "☀ Light" : "🌙 Dark";
}
