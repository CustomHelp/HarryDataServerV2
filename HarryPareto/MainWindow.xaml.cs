using System.Windows;
using HarryShared.Theming;

namespace HarryPareto;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        ThemeManager.Initialize();
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        UpdateThemeButton();
        Loaded += async (_, _) => await _vm.StartupAsync();
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeButton();
    }

    private void UpdateThemeButton() =>
        ThemeToggle.Content = ThemeManager.Current == AppTheme.Dark ? "☀ Light" : "🌙 Dark";
}
