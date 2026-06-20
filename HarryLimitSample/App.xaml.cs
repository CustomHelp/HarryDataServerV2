using System.Windows;
using HarryShared.Config;
using HarryShared.Data;

namespace HarryLimitSample;

/// <summary>Entry point for HarryLimitSample (read-only GetData for loading, writes MSA JSON).</summary>
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
                $"HarryLimitSample failed to start:\n\n{ex.Message}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
