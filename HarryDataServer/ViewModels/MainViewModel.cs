using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HarryDataServer.Communication;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>Table name + approximate row count for the Database tab.</summary>
public sealed record TableCountVm(string Name, string Rows);

/// <summary>
/// Root dashboard view model. Aggregates a read-only mirror of every subsystem,
/// refreshed by a single 1 s UI-thread timer (row counts every 30 s). Background
/// service events (SPS activity, log) are captured into thread-safe buffers and
/// synced into the bound collections on the timer tick.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IConfigService _config;
    private readonly ICameraService _cameras;
    private readonly IDatabaseService _database;
    private readonly IMeasurementProcessor _measurements;
    private readonly ISettingsProcessor _settings;
    private readonly IDiagnosticProcessor _diagnostics;
    private readonly IPartExitOrchestrator _partExit;
    private readonly ICsvService _csv;
    private readonly ICollageService _collage;
    private readonly IMsaService _msa;
    private readonly ISpsServer _sps;
    private readonly ISystemHealth _health;
    private readonly ILogBuffer _log;

    private readonly Dictionary<SpsChannel, SpsChannelViewModel> _channelByKey = new();
    private readonly DispatcherTimer _timer;
    private readonly DateTime _startUtc = DateTime.UtcNow;

    private string _lastHealthKey = string.Empty;
    private bool _rowCountsBusy;
    private DateTime _lastRowCountsUtc = DateTime.MinValue;
    private bool _msaLoaded;
    private bool _productionBusy;
    private DateTime _lastProductionUtc = DateTime.MinValue;

    public MainViewModel(
        IConfigService config, ICameraService cameras, IDatabaseService database,
        IMeasurementProcessor measurements, ISettingsProcessor settings, IDiagnosticProcessor diagnostics,
        IPartExitOrchestrator partExit, ICsvService csv, ICollageService collage, IMsaService msa,
        ISpsServer sps, ISystemHealth health, ILogBuffer log)
    {
        _config = config; _cameras = cameras; _database = database;
        _measurements = measurements; _settings = settings; _diagnostics = diagnostics;
        _partExit = partExit; _csv = csv; _collage = collage; _msa = msa;
        _sps = sps; _health = health; _log = log;

        ConfigFile = System.IO.Path.GetFileName(_config.IniPath);
        AppVersion = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.0");

        Cameras = new ObservableCollection<CameraViewModel>(_cameras.Clients.Select(c => new CameraViewModel(c)));
        SpsChannels = BuildChannels();
        Msa = new MsaViewModel(_msa);
        Log = new LogViewModel(_log, _config);

        _sps.ChannelActivity += OnChannelActivity;

        Refresh();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    // --- Top bar / shell ---
    public string AppName => "HarryDataServer";
    public string AppVersion { get; }
    public string ConfigFile { get; }

    public ObservableCollection<CameraViewModel> Cameras { get; }
    public ObservableCollection<SpsChannelViewModel> SpsChannels { get; }
    public MsaViewModel Msa { get; }
    public LogViewModel Log { get; }
    public ObservableCollection<string> HealthFaults { get; } = new();
    public ObservableCollection<TableCountVm> TableRows { get; } = new();
    public ObservableCollection<string> OfflineCameras { get; } = new();

    // --- Overview ---
    [ObservableProperty] private string _overviewCameras = "0 / 0 cameras online";
    [ObservableProperty] private Brush _overviewCamerasBrush = Brushes.Gray;
    [ObservableProperty] private string _todayOk = "0";
    [ObservableProperty] private string _todayNg = "0";
    [ObservableProperty] private string _lastPartExit = "—";
    [ObservableProperty] private string _activeOrder = "—";
    [ObservableProperty] private bool _hasActiveErrors;

    [ObservableProperty] private string _systemStatus = "Starting…";
    [ObservableProperty] private Brush _systemStatusBrush = Brushes.Gray;
    [ObservableProperty] private string _errorCountText = "0 errors";
    [ObservableProperty] private string _uptimeText = "0:00:00";

    // --- Health banner ---
    [ObservableProperty] private string _healthWord = "OK";
    [ObservableProperty] private string _healthMessage = string.Empty;
    [ObservableProperty] private Brush _healthBrush = Brushes.Gray;
    [ObservableProperty] private bool _isHealthy = true;

    // --- Database ---
    [ObservableProperty] private string _databaseStatusText = "—";
    [ObservableProperty] private Brush _databaseBrush = Brushes.Gray;
    [ObservableProperty] private Brush _connectionLed = Brushes.Gray;
    [ObservableProperty] private Brush _tablesLed = Brushes.Gray;
    [ObservableProperty] private string _retentionInfo = string.Empty;

    // --- Cameras / SPS summary ---
    [ObservableProperty] private string _cameraSummary = "0 / 0 connected";

    // --- CSV ---
    [ObservableProperty] private string _csvActivePath = "(no file yet)";
    [ObservableProperty] private string _csvRows = "0 rows";
    [ObservableProperty] private string _csvLastWrite = "never";
    [ObservableProperty] private string _exportTiming = "Last export: —";

    // --- Pipelines (Database tab) ---
    [ObservableProperty] private string _measurementsText = "—";
    [ObservableProperty] private string _settingsText = "—";
    [ObservableProperty] private string _partExitText = "—";
    [ObservableProperty] private string _diagnosticsText = "—";
    [ObservableProperty] private string _collageText = "—";

    private ObservableCollection<SpsChannelViewModel> BuildChannels()
    {
        var sps = _config.Config.Sps;
        var defs = new (SpsChannel Ch, int Port)[]
        {
            (SpsChannel.KeepAlive, sps.PortKeepAlive),
            (SpsChannel.PartExit, sps.PortPartExit),
            (SpsChannel.MsaM10, sps.PortMsaM10),
            (SpsChannel.MsaM11, sps.PortMsaM11),
            (SpsChannel.MsaM20, sps.PortMsaM20),
            (SpsChannel.MsaM21, sps.PortMsaM21),
            (SpsChannel.MsaM50, sps.PortMsaM50),
        };

        var list = new ObservableCollection<SpsChannelViewModel>();
        foreach (var (ch, port) in defs)
        {
            var vm = new SpsChannelViewModel(_sps, ch, port);
            _channelByKey[ch] = vm;
            list.Add(vm);
        }
        return list;
    }

    private void OnChannelActivity(SpsChannel channel, bool isResponse, string text)
    {
        if (_channelByKey.TryGetValue(channel, out var vm))
            vm.Record(isResponse, text);
    }

    private void Refresh()
    {
        foreach (var cam in Cameras) cam.Update();
        foreach (var ch in SpsChannels) ch.Update();

        CameraSummary = $"{_cameras.ConnectedCount} / {_cameras.TotalCount} connected";

        RefreshDatabase();
        RefreshCsv();
        RefreshPipelines();
        RefreshHealth();
        RefreshTopBar();
        RefreshOverviewCameras();
        Log.Tick();
        MaybeRefreshRowCounts();
        MaybeRefreshProduction();
        MaybeLoadMsa();
    }

    private void RefreshOverviewCameras()
    {
        var connected = _cameras.ConnectedCount;
        var total = _cameras.TotalCount;
        OverviewCameras = $"{connected} / {total} cameras online";
        OverviewCamerasBrush = connected == total ? Led.Green : connected == 0 ? Led.Red : Led.Orange;

        OfflineCameras.Clear();
        foreach (var cam in Cameras)
            if (!cam.ConnectedBrush.Equals(Led.Green))
                OfflineCameras.Add($"{cam.Name} ({cam.StateText})");
    }

    private async void MaybeRefreshProduction()
    {
        if (_productionBusy || _database.Status != DatabaseStatus.Ready)
            return;
        if ((DateTime.UtcNow - _lastProductionUtc).TotalSeconds < 5)
            return;

        _productionBusy = true;
        try
        {
            var snap = await _database.GetProductionSnapshotAsync().ConfigureAwait(true);
            TodayOk = snap.TodayOk.ToString("N0");
            TodayNg = snap.TodayNg.ToString("N0");
            LastPartExit = snap.LastPartExit is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss") : "—";
            ActiveOrder = string.IsNullOrWhiteSpace(snap.ActiveOrder) ? "—" : snap.ActiveOrder;
            _lastProductionUtc = DateTime.UtcNow;
        }
        catch { /* non-critical */ }
        finally { _productionBusy = false; }
    }

    private void RefreshDatabase()
    {
        var status = _database.Status;
        DatabaseStatusText = $"{_config.Config.MySql.Database}: {status}";
        var ready = status == DatabaseStatus.Ready;
        DatabaseBrush = ready ? Led.Green : status == DatabaseStatus.Failed ? Led.Red : Led.Orange;
        ConnectionLed = ready ? Led.Green : status == DatabaseStatus.Failed ? Led.Red : Led.Orange;
        TablesLed = ready ? Led.Green : Led.Gray;

        var mysql = _config.Config.MySql;
        var nas = _config.Config.Nas;
        RetentionInfo =
            $"DB partitions: {mysql.RetentionPeriodDays} days  ·  " +
            $"NAS NG: {nas.RetentionNgDays}d  ·  Diagnostic: {nas.RetentionDiagnosticDays}d  ·  GoldenSample: {nas.RetentionGoldenSampleDays}d";
    }

    private void RefreshCsv()
    {
        CsvActivePath = string.IsNullOrEmpty(_csv.ActiveFilePath) ? "(no file yet)" : _csv.ActiveFilePath!;
        CsvRows = $"{_csv.TotalRows:N0} rows  ·  queue {_csv.PendingCount:N0}";
        CsvLastWrite = _csv.LastWriteTime is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss") : "never";
    }

    private void RefreshPipelines()
    {
        MeasurementsText = Stat(_measurements.TotalInserted, _measurements.PendingCount, "written");
        SettingsText = Stat(_settings.TotalInserted, _settings.PendingCount, "written");
        PartExitText = $"{_partExit.TotalProcessed:N0} parts processed";
        ExportTiming = "Last export: " + _partExit.LastTiming;
        DiagnosticsText = Stat(_diagnostics.TotalWritten, _diagnostics.PendingCount, "written");
        var collageState = _config.Config.Collage.Generate ? "enabled" : "disabled";
        CollageText = $"{_collage.TotalGenerated:N0} generated  ·  queue {_collage.PendingCount:N0}  ·  {collageState}";
    }

    private static string Stat(long total, int pending, string noun) => $"{total:N0} {noun}  ·  queue {pending:N0}";

    private void RefreshHealth()
    {
        var snapshot = _health.Snapshot();
        IsHealthy = snapshot.IsHealthy;
        HasActiveErrors = !snapshot.IsHealthy;
        HealthWord = snapshot.SignalWord;
        HealthMessage = snapshot.Message;
        HealthBrush = snapshot.Worst switch
        {
            HealthSeverity.Error => Led.Red,
            HealthSeverity.Warning => Led.Orange,
            _ => Led.Green,
        };

        var key = string.Join("|", snapshot.Faults.Select(f => $"{f.Severity}:{f.Source}:{f.Message}"));
        if (key == _lastHealthKey)
            return;
        _lastHealthKey = key;

        HealthFaults.Clear();
        foreach (var fault in snapshot.Faults)
            HealthFaults.Add($"[{fault.Severity}] {fault.Source}: {fault.Message}");
    }

    private void RefreshTopBar()
    {
        var snapshot = _health.Snapshot();
        (SystemStatus, SystemStatusBrush) = snapshot.Worst switch
        {
            HealthSeverity.Error => ("FAULT", Led.Red),
            HealthSeverity.Warning => ("DEGRADED", Led.Orange),
            _ => ("All systems nominal", Led.Green),
        };

        ErrorCountText = $"{_log.ErrorCount} errors · {_log.WarningCount} warnings";

        var up = DateTime.UtcNow - _startUtc;
        UptimeText = $"{(int)up.TotalHours}:{up.Minutes:00}:{up.Seconds:00}";
    }

    private async void MaybeRefreshRowCounts()
    {
        if (_rowCountsBusy || _database.Status != DatabaseStatus.Ready)
            return;
        if ((DateTime.UtcNow - _lastRowCountsUtc).TotalSeconds < 30)
            return;

        _rowCountsBusy = true;
        try
        {
            var counts = await _database.GetRowCountsAsync().ConfigureAwait(true);
            TableRows.Clear();
            foreach (var table in DatabaseSchema.Tables)
                TableRows.Add(new TableCountVm(table.Name, counts.GetValueOrDefault(table.Name).ToString("N0")));
            _lastRowCountsUtc = DateTime.UtcNow;
        }
        catch { /* surfaced via health/log elsewhere */ }
        finally { _rowCountsBusy = false; }
    }

    private async void MaybeLoadMsa()
    {
        if (_msaLoaded || _database.Status != DatabaseStatus.Ready)
            return;
        _msaLoaded = true;
        try { await Msa.LoadAllAsync().ConfigureAwait(true); }
        catch { /* logged in service */ }
    }
}
