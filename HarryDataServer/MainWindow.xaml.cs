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
    private readonly ICameraService _cameras;
    private readonly IMeasurementProcessor _measurements;

    public MainWindow(
        IConfigService config,
        ILogService log,
        IDatabaseService database,
        ICameraService cameras,
        IMeasurementProcessor measurements)
    {
        InitializeComponent();
        _config = config;
        _log = log;
        _database = database;
        _cameras = cameras;
        _measurements = measurements;

        _database.StatusChanged += OnDatabaseStatusChanged;
        _cameras.StatusChanged += OnCameraStatusChanged;
        _measurements.StatsChanged += OnMeasurementStatsChanged;

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _database.StatusChanged -= OnDatabaseStatusChanged;
            _cameras.StatusChanged -= OnCameraStatusChanged;
            _measurements.StatsChanged -= OnMeasurementStatsChanged;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateStatus(_database.Status);
        UpdateCameraStatus();
        UpdateMeasurementStatus();
        _log.Information("Main window loaded.");
    }

    private void OnDatabaseStatusChanged(DatabaseStatus status) =>
        Dispatcher.Invoke(() => UpdateStatus(status));

    private void OnCameraStatusChanged() =>
        Dispatcher.Invoke(UpdateCameraStatus);

    private void OnMeasurementStatsChanged() =>
        Dispatcher.Invoke(UpdateMeasurementStatus);

    private void UpdateStatus(DatabaseStatus dbStatus)
    {
        var cameraCount = _config.Config.Cameras.Count;
        StatusText.Text =
            $"Cameras: {cameraCount} (from {Path.GetFileName(_config.IniPath)})  |  " +
            $"Database '{_config.Config.MySql.Database}': {dbStatus}";
    }

    private void UpdateCameraStatus() =>
        CameraStatusText.Text = $"Cameras connected: {_cameras.ConnectedCount}/{_cameras.TotalCount}";

    private void UpdateMeasurementStatus() =>
        MeasurementStatusText.Text =
            $"Measurements written: {_measurements.TotalInserted:N0} (queue {_measurements.PendingCount:N0})";
}
