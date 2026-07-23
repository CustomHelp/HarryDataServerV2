using System.Windows;
using HarryShared.Theming;

namespace HarryCounter;

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
        if (HarryShared.Config.HarryConfig.ShowChangeDialog("HarryCounter"))
            MessageBox.Show(this,
                "Config path saved. Please restart the tool so the new Harry.ini/database becomes active.",
                "Restart required", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeButton();
    }

    private void UpdateThemeButton() =>
        ThemeToggle.Content = ThemeManager.Current == AppTheme.Dark ? "☀ Light" : "🌙 Dark";
}
