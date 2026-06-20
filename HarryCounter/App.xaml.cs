using System.Windows;
using HarryShared.Config;
using HarryShared.Data;

namespace HarryCounter;

/// <summary>Entry point for HarryCounter (read-only GetData account).</summary>
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
                $"HarryCounter failed to start:\n\n{ex.Message}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
