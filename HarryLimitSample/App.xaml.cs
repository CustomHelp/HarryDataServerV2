using System.Windows;
using HarryShared.Communication;
using HarryShared.Config;
using HarryShared.Data;
using HarryShared.Splash;

namespace HarryLimitSample;

/// <summary>Entry point for HarryLimitSample (read-only GetData for loading, writes MSA JSON).</summary>
public partial class App : Application
{
    private ScannerCompanionClient? _scanner;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = SplashHost.ShowFast("LIMIT SAMPLE");
        try
        {
            var config = HarryConfig.Load();
            var query = new QueryService(config);
            _scanner = new ScannerCompanionClient(config.ScannerHost, config.ScannerPort);
            var vm = new MainViewModel(query, config, _scanner);
            var window = new MainWindow { DataContext = vm };
            window.Show();

            // Start the DMC scanner bridge client (auto-reconnect); scans are received in the
            // background and, when the Active toggle is on, replayed into the existing search.
            _scanner.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"HarryLimitSample failed to start:\n\n{ex.Message}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
        finally
        {
            splash.Close();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _scanner?.StopAsync().Wait(TimeSpan.FromSeconds(2)); }
        catch { /* best-effort shutdown */ }
        base.OnExit(e);
    }
}
