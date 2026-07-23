using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HarryShared.Theming;

namespace HarryAnalysis;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Initialize();
        UpdateThemeButton();
        Loaded += (_, _) => ScanBox.Focus();
        // Right-click selects the row under the cursor so the context menu acts on it.
        HistoryGrid.PreviewMouseRightButtonDown += OnHistoryRightClick;
    }

    private void OnChangeConfig(object sender, RoutedEventArgs e)
    {
        if (HarryShared.Config.HarryConfig.ShowChangeDialog("HarryAnalysis"))
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

    private static void OnHistoryRightClick(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow row)
            row.IsSelected = true;
    }
}
