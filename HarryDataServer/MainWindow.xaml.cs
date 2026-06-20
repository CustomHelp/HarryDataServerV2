using System.IO;
using System.Windows;
using System.Windows.Media;
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
    private readonly ISpsServer _sps;
    private readonly ISystemHealth _health;

    public MainWindow(
        IConfigService config,
        ILogService log,
        IDatabaseService database,
        ICameraService cameras,
        IMeasurementProcessor measurements,
        ISpsServer sps,
        ISystemHealth health)
    {
        InitializeComponent();
        _config = config;
        _log = log;
        _database = database;
        _cameras = cameras;
        _measurements = measurements;
        _sps = sps;
        _health = health;

        _database.StatusChanged += OnDatabaseStatusChanged;
        _cameras.StatusChanged += OnCameraStatusChanged;
        _measurements.StatsChanged += OnMeasurementStatsChanged;
        _sps.StatusChanged += OnSpsStatusChanged;
        _health.Changed += OnHealthChanged;

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _database.StatusChanged -= OnDatabaseStatusChanged;
            _cameras.StatusChanged -= OnCameraStatusChanged;
            _measurements.StatsChanged -= OnMeasurementStatsChanged;
            _sps.StatusChanged -= OnSpsStatusChanged;
            _health.Changed -= OnHealthChanged;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateStatus(_database.Status);
        UpdateCameraStatus();
        UpdateMeasurementStatus();
        UpdateSpsStatus();
        UpdateHealthStatus();
        _log.Information("Main window loaded.");
    }

    private void OnDatabaseStatusChanged(DatabaseStatus status) =>
        Dispatcher.Invoke(() => UpdateStatus(status));

    private void OnCameraStatusChanged() =>
        Dispatcher.Invoke(UpdateCameraStatus);

    private void OnMeasurementStatsChanged() =>
        Dispatcher.Invoke(UpdateMeasurementStatus);

    private void OnSpsStatusChanged() =>
        Dispatcher.Invoke(UpdateSpsStatus);

    private void OnHealthChanged() =>
        Dispatcher.Invoke(UpdateHealthStatus);

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

    private void UpdateSpsStatus() =>
        SpsStatusText.Text = _sps.IsRunning
            ? $"SPS: {_sps.ListeningChannels}/7 ch, {_sps.ActiveConnections} conn"
            : "SPS: stopped";

    private void UpdateHealthStatus()
    {
        var health = _health.Snapshot();
        if (health.IsHealthy)
        {
            HealthStatusText.Text = "Health: OK";
            HealthStatusText.Foreground = Brushes.Green;
            return;
        }

        HealthStatusText.Text = $"Health: {health.SignalWord} — {health.Message}";
        HealthStatusText.Foreground = health.Worst == HealthSeverity.Error ? Brushes.Red : Brushes.DarkOrange;
    }
}
