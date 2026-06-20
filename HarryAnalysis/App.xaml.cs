using System.Windows;
using HarryShared.Config;
using HarryShared.Data;

namespace HarryAnalysis;

/// <summary>
/// Entry point for the HarryAnalysis scanner tool. Loads Harry.ini (read-only
/// GetData account), composes the query service + main view model, and shows the
/// window. A missing/invalid config is surfaced to the operator instead of crashing.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var config = HarryConfig.Load();
            var query = new QueryService(config);
            var vm = new MainViewModel(query, config);
            var window = new MainWindow { DataContext = vm };
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"HarryAnalysis failed to start:\n\n{ex.Message}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
