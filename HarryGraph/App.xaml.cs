using System.Windows;
using HarryShared.Config;
using HarryShared.Data;
using HarryShared.Splash;

namespace HarryGraph;

/// <summary>
/// Entry point for HarryGraph. Loads Harry.ini (read-only GetData account), builds
/// the query service + view model and shows the window.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = SplashHost.ShowFast("GRAPH");
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
                $"HarryGraph failed to start:\n\n{ex.Message}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
        finally
        {
            splash.Close();
        }
    }
}
