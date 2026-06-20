using System.Windows;
using HarryShared.Config;

namespace HarryCollageCreator;

/// <summary>
/// Entry point for HarryCollageCreator. Harry.ini is optional here (the tool only
/// reads/writes Collage.ini files), so a missing config is non-fatal — it just
/// leaves the default Collage.ini path blank.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        HarryConfig? config = null;
        try { config = HarryConfig.Load(); }
        catch { /* config optional for this tool */ }

        var vm = new MainViewModel(config);
        var window = new MainWindow { DataContext = vm };
        window.Show();
    }
}
