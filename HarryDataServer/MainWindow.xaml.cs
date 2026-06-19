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
    private readonly IDatabaseService _database;

    public MainWindow(IConfigService config, ILogService log, IDatabaseService database)
    {
        InitializeComponent();
        _config = config;
        _log = log;
        _database = database;

        _database.StatusChanged += OnDatabaseStatusChanged;

        Loaded += OnLoaded;
        Closed += (_, _) => _database.StatusChanged -= OnDatabaseStatusChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateStatus(_database.Status);
        _log.Information("Main window loaded.");
    }

    private void OnDatabaseStatusChanged(DatabaseStatus status) =>
        Dispatcher.Invoke(() => UpdateStatus(status));

    private void UpdateStatus(DatabaseStatus dbStatus)
    {
        var cameraCount = _config.Config.Cameras.Count;
        StatusText.Text =
            $"Cameras: {cameraCount} (from {Path.GetFileName(_config.IniPath)})  |  " +
            $"Database '{_config.Config.MySql.Database}': {dbStatus}";
    }
}
