using System.IO;
using System.Windows;
using HarryDataServer.Services;

namespace HarryDataServer;

/// <summary>
/// Main application window. Hosts the per-subsystem tabs. UserControls are added
/// to each tab in later UI phases (CLAUDE.md section 14).
/// </summary>
public partial class MainWindow : Window
{
    private readonly IConfigService _config;
    private readonly ILogService _log;

    public MainWindow(IConfigService config, ILogService log)
    {
        InitializeComponent();
        _config = config;
        _log = log;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var cameraCount = _config.Config.Cameras.Count;
        StatusText.Text =
            $"Loaded {cameraCount} camera(s) from {Path.GetFileName(_config.IniPath)} — Database: {_config.Config.MySql.Database}";
        _log.Information("Main window loaded.");
    }
}
