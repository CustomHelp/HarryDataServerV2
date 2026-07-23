using System.Windows;
using HarryShared.Theming;

namespace HarryLimitSample;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Initialize();
        UpdateThemeButton();
        Loaded += (_, _) => ScanBox.Focus();
    }

    private void OnChangeConfig(object sender, RoutedEventArgs e)
    {
        if (HarryShared.Config.HarryConfig.ShowChangeDialog("HarryLimitSample"))
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
