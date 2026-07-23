using System.Windows;
using HarryShared.Communication;
using HarryShared.Config;
using HarryShared.Data;
using HarryShared.Splash;

namespace HarryAnalysis;

/// <summary>
/// Entry point for the HarryAnalysis scanner tool. Loads Harry.ini (read-only
/// GetData account), composes the query service + main view model, and shows the
/// window. A missing/invalid config is surfaced to the operator instead of crashing.
/// </summary>
public partial class App : Application
{
    private ScannerCompanionClient? _scanner;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = SplashHost.ShowFast("ANALYSIS");
        try
        {
            var config = HarryConfig.LoadInteractive("HarryAnalysis");
            if (config is null) { Shutdown(0); return; } // user cancelled the config picker
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
                $"HarryAnalysis failed to start:\n\n{ex.Message}",
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
