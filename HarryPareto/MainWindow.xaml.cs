using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>Re-clicking the already-selected module bar clears the filter (task C3): WPF keeps a
    /// selected item selected on re-click, so intercept it here and null the selection.</summary>
    private void OnModuleBarPreviewClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list)
            return;
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is ListBoxItem item && ReferenceEquals(item.DataContext, list.SelectedItem))
        {
            list.SelectedItem = null; // toggle the filter off
            e.Handled = true;
        }
    }

    private void UpdateThemeButton() =>
        ThemeToggle.Content = ThemeManager.Current == AppTheme.Dark ? "☀ Light" : "🌙 Dark";
}
