using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Data;
using HarryShared.Help;
using Microsoft.Win32;

namespace HarryPareto;

/// <summary>One horizontal Pareto bar (a defect feature). The bar is drawn by <see cref="SegmentBar"/>
/// as a stack of per-camera segments (task B), so a KF1/KF3 skew stays visible.</summary>
public sealed class ParetoBarItem
{
    public required string Label { get; init; }
    public required string ToolTipText { get; init; }
    public required string ModuleRef { get; init; }
    public int AffectedParts { get; init; }
    public int Occurrences { get; init; }
    public required string CountText { get; init; }

    /// <summary>Per-camera coloured segments filling the bar (task B); one segment in the split view.</summary>
    public required IReadOnlyList<BarSegment> Segments { get; init; }
    /// <summary>Empty tail weight so every bar shares one scale (weights relative to the largest bar).</summary>
    public double RemainderWeight { get; init; }

    public required string TrendGlyph { get; init; }
    public required Brush TrendBrush { get; init; }

    /// <summary>The measurement definitions this bar aggregates — used for the origin query (task A).</summary>
    public required IReadOnlyList<int> DefinitionIds { get; init; }
    /// <summary>True when module_ref maps to origin columns in dmcserial (M1x/M2x/M3x) — task A.</summary>
    public bool CanShowOrigin { get; init; }
}

/// <summary>One bar of the per-module share chart (click filters the Pareto list).</summary>
public sealed class ModuleBarItem
{
    public required string ModuleRef { get; init; }
    public int AffectedParts { get; init; }
    public required string CountText { get; init; }
    public required Brush BarBrush { get; init; }
    public double BarFraction { get; init; }
}

/// <summary>Legend entry mapping a module_ref to its bar colour.</summary>
public sealed record LegendItem(string ModuleRef, Brush Brush);

/// <summary>One nest cell of the origin matrix (task A): affected/inspected + rate for a module×nest.</summary>
public sealed class OriginCellVm
{
    public required string Text { get; init; }       // "3/40" (affected/inspected)
    public required string RateText { get; init; }   // "7.5 %"
    public bool IsEmpty { get; init; }
    public bool IsElevated { get; init; }            // clearly-above-average rate → highlight
}

/// <summary>One origin-module row of the matrix, its cells aligned to <see cref="MainViewModel.OriginNests"/>.</summary>
public sealed class OriginRowVm
{
    public required string Module { get; init; }
    public required IReadOnlyList<OriginCellVm> Cells { get; init; }
}

/// <summary>
/// Live Top-20 Pareto of production defect reasons. Loads read-only defect data from
/// <see cref="ParetoDb"/>, refreshes on a timer, and aggregates it in memory into the bound view:
/// bars aggregated per station across cameras (task B) and per sensor family (task C, both default
/// ON), a per-module share chart, the shift comparison, and — on a bar click — the origin matrix
/// (module × nest, with rate, task A). DB errors surface as a status message + red lamp, never a
/// crash.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int TopN = 20;

    // Categorical palette (readable in dark and light); assigned per module_ref (colour + legend).
    private static readonly string[] Palette =
    {
        "#4E79A7", "#F28E2B", "#59A14F", "#E15759", "#76B7B2",
        "#EDC948", "#B07AA1", "#FF9DA7", "#9C755F", "#BAB0AC",
    };

    private static readonly Regex SensorSuffix = new(@"_S(\d+)$", RegexOptions.Compiled);

    private ParetoSettings _settings;
    private ParetoDb _db;
    private readonly DispatcherTimer _timer;
    private bool _busy;

    public MainViewModel()
    {
        _settings = ParetoSettings.Load();
        _db = new ParetoDb(_settings);
        RefreshSeconds = _settings.RefreshSeconds;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(5, RefreshSeconds)) };
        _timer.Tick += async (_, _) => await RefreshAsync();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ReconnectCommand = new RelayCommand(OpenConnectionDialog);
        ExportCsvCommand = new RelayCommand(ExportCsv);
        ClearModuleFilterCommand = new RelayCommand(() => SelectedModuleBar = null);
        ResetViewCommand = new RelayCommand(ResetView);
        ToggleTvCommand = new RelayCommand(() => TvMode = !TvMode);
        ShowHelpCommand = new RelayCommand(ShowHelp);
        ShowOriginCommand = new AsyncRelayCommand<ParetoBarItem>(ShowOriginAsync);
        CloseOriginCommand = new RelayCommand(() => HasOriginDetail = false);
    }

    // --- Bound collections -------------------------------------------------

    public ObservableCollection<ParetoBarItem> Bars { get; } = new();
    public ObservableCollection<ModuleBarItem> ModuleBars { get; } = new();
    public ObservableCollection<LegendItem> Legend { get; } = new();
    public ObservableCollection<string> Status2Warnings { get; } = new();
    public ObservableCollection<string> OriginNests { get; } = new();
    public ObservableCollection<OriginRowVm> OriginRows { get; } = new();

    public ObservableCollection<string> WindowOptions { get; } =
        new() { "Shift", "30 min", "1 h", "2 h", "4 h", "8 h", "16 h", "1 day", "2 days", "3 days", "7 days" };
    public ObservableCollection<string> Controllers { get; } = new() { "(all)" };

    // --- Commands ----------------------------------------------------------

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand ReconnectCommand { get; }
    public IRelayCommand ExportCsvCommand { get; }
    public IRelayCommand ClearModuleFilterCommand { get; }
    public IRelayCommand ResetViewCommand { get; }
    public IRelayCommand ToggleTvCommand { get; }
    public IRelayCommand ShowHelpCommand { get; }
    public IAsyncRelayCommand<ParetoBarItem> ShowOriginCommand { get; }
    public IRelayCommand CloseOriginCommand { get; }

    // --- KPI head ----------------------------------------------------------

    [ObservableProperty] private string _inspectedText = "—";
    [ObservableProperty] private string _badText = "—";
    [ObservableProperty] private string _rateText = "—";
    [ObservableProperty] private string _windowText = "—";
    [ObservableProperty] private string _updatedText = "—";
    [ObservableProperty] private string _connectionText = "nicht verbunden";
    [ObservableProperty] private Brush _connectionBrush = Brushes.Gray;
    [ObservableProperty] private string _shiftComparisonText = "—";
    [ObservableProperty] private string _statusMessage = "Bereit.";
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty] private string _moduleFilterText = string.Empty;
    [ObservableProperty] private bool _hasModuleFilter;
    [ObservableProperty] private double _uiScale = 1.0;

    // --- Origin detail panel (task A) --------------------------------------

    [ObservableProperty] private bool _hasOriginDetail;
    [ObservableProperty] private string _originTitle = string.Empty;
    [ObservableProperty] private string _originSubtitle = string.Empty;
    [ObservableProperty] private string _originNote = string.Empty;
    [ObservableProperty] private bool _hasOriginNote;
    [ObservableProperty] private bool _hasOriginMatrix;

    public string AppName => "HarryPareto — Live Top-20 defect reasons";
    public string AppVersion => "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "2.0.0");

    // --- Filters / view toggles (trigger a rebuild) -----------------------

    private string _selectedWindow = "Shift";
    public string SelectedWindow
    {
        get => _selectedWindow;
        set { if (SetProperty(ref _selectedWindow, value)) _ = RefreshAsync(); }
    }

    private string _selectedController = "(all)";
    public string SelectedController
    {
        get => _selectedController;
        set { if (SetProperty(ref _selectedController, value)) _ = RefreshAsync(); }
    }

    // Task B: aggregate KF1/KF3 of the same station by default; toggle ON to separate by camera.
    private bool _separateByCamera;
    public bool SeparateByCamera
    {
        get => _separateByCamera;
        set { if (SetProperty(ref _separateByCamera, value)) { HasOriginDetail = false; RebuildBars(); } }
    }

    // Task C: group _S1.._S5 into a sensor family by default; toggle ON to see sensors individually.
    private bool _sensorsIndividual;
    public bool SensorsIndividual
    {
        get => _sensorsIndividual;
        set { if (SetProperty(ref _sensorsIndividual, value)) { HasOriginDetail = false; RebuildBars(); } }
    }

    private ModuleBarItem? _selectedModuleBar;
    public ModuleBarItem? SelectedModuleBar
    {
        get => _selectedModuleBar;
        set
        {
            if (SetProperty(ref _selectedModuleBar, value))
            {
                HasModuleFilter = value is not null;
                ModuleFilterText = value is null ? string.Empty : $"Module filter: {value.ModuleRef}";
                HasOriginDetail = false;
                RebuildBars();
            }
        }
    }

    private int _refreshSeconds = 30;
    public int RefreshSeconds
    {
        get => _refreshSeconds;
        set
        {
            var v = Math.Max(5, value);
            if (SetProperty(ref _refreshSeconds, v))
            {
                _settings.RefreshSeconds = v;
                if (_timer is not null) _timer.Interval = TimeSpan.FromSeconds(v);
            }
        }
    }

    private bool _autoRefresh = true;
    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            if (SetProperty(ref _autoRefresh, value))
            {
                if (value) _timer.Start(); else _timer.Stop();
            }
        }
    }

    private bool _tvMode;
    public bool TvMode
    {
        get => _tvMode;
        set { if (SetProperty(ref _tvMode, value)) UiScale = value ? 1.5 : 1.0; }
    }

    // Raw current/previous window defect parts, cached so toggles + the module filter re-slice in
    // memory without a DB round-trip.
    private List<DefectPart> _defectParts = new();
    private List<DefectPart> _prevDefectParts = new();
    private Dictionary<string, Brush> _moduleColors = new(StringComparer.OrdinalIgnoreCase);
    private int _inspected;
    private DateTime _winFrom;
    private DateTime _winTo;

    // --- Startup -----------------------------------------------------------

    /// <summary>Called once from the window: auto-connect with the saved settings, else show the dialog.</summary>
    public async Task StartupAsync()
    {
        var ok = await _db.CanConnectAsync().ConfigureAwait(true);
        if (!ok)
        {
            StatusMessage = "Auto-connect failed — please check the connection data.";
            SetConnection(false, "Auto-connect failed");
            OpenConnectionDialog();
            return;
        }

        await AfterConnectAsync().ConfigureAwait(true);
    }

    private async Task AfterConnectAsync()
    {
        await LoadControllersAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        if (AutoRefresh) _timer.Start();
    }

    private async Task LoadControllersAsync()
    {
        try
        {
            var names = await _db.GetControllersAsync().ConfigureAwait(true);
            var keep = SelectedController;
            Controllers.Clear();
            Controllers.Add("(all)");
            foreach (var n in names) Controllers.Add(n);
            _selectedController = Controllers.Contains(keep) ? keep : "(all)";
            OnPropertyChanged(nameof(SelectedController));
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not load the controller list: " + ex.Message;
        }
    }

    // --- Refresh -----------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            var now = DateTime.Now;
            var (from, to) = WindowRange(now);
            var span = to - from;
            var prevFrom = from - span;
            var ctrl = SelectedController == "(all)" ? null : SelectedController;
            _winFrom = from;
            _winTo = to;

            var kpi = await _db.GetKpiAsync(from, to, ctrl).ConfigureAwait(true);
            _defectParts = await _db.GetDefectPartsAsync(from, to, ctrl).ConfigureAwait(true);
            _prevDefectParts = await _db.GetDefectPartsAsync(prevFrom, from, ctrl).ConfigureAwait(true);
            var status2 = await _db.GetStatus2Async(from, to, ctrl).ConfigureAwait(true);

            // Shift comparison: current shift vs previous shift bad-rate.
            var (csFrom, csTo) = CurrentShift(now);
            var psFrom = csFrom - (csTo - csFrom);
            var kpiShift = await _db.GetKpiAsync(csFrom, csTo, ctrl).ConfigureAwait(true);
            var kpiPrevShift = await _db.GetKpiAsync(psFrom, csFrom, ctrl).ConfigureAwait(true);

            _inspected = kpi.Inspected;
            RebuildModuleColors();

            InspectedText = kpi.Inspected.ToString("N0", CultureInfo.CurrentCulture);
            BadText = kpi.Bad.ToString("N0", CultureInfo.CurrentCulture);
            RateText = kpi.RatePct.ToString("0.0", CultureInfo.CurrentCulture) + " %";
            WindowText = $"{SelectedWindow}  ({from:dd.MM. HH:mm} – {to:HH:mm})";
            UpdatedText = now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture);
            ShiftComparisonText =
                $"Shift {kpiShift.RatePct:0.0} % ({kpiShift.Bad}/{kpiShift.Inspected})   ·   " +
                $"Previous shift {kpiPrevShift.RatePct:0.0} % ({kpiPrevShift.Bad}/{kpiPrevShift.Inspected})   ·   " +
                $"{Delta(kpiShift.RatePct - kpiPrevShift.RatePct)}";

            RebuildModuleBars();
            RebuildBars();
            RebuildWarnings(status2);
            HasOriginDetail = false; // stale after a data refresh

            SetConnection(true, $"connected — {_settings.User}@{_settings.Ip}:{_settings.Port}/{_settings.Database}");
            StatusMessage = $"Refreshed {now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            SetConnection(false, "Connection / query error");
            StatusMessage = "Query error: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private static string Delta(double d) =>
        d > 0.05 ? $"▲ +{d:0.0} %-pts" : d < -0.05 ? $"▼ {d:0.0} %-pts" : "■ ±0";

    // --- Aggregation (tasks B + C) -----------------------------------------

    /// <summary>One aggregated Pareto group under the current camera/sensor toggles.</summary>
    private sealed class AggGroup
    {
        public required string CameraKey { get; init; }   // station (default) or controller
        public required string FeatureName { get; init; } // sensor family (default) or display_name
        public required string ModuleRef { get; init; }
        public string Key => CameraKey + "" + FeatureName;
        public HashSet<string> Serials { get; } = new();                       // distinct affected parts
        public int Occurrences { get; set; }
        public HashSet<int> DefIds { get; } = new();
        public Dictionary<string, HashSet<string>> ByCamera { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, HashSet<string>> BySensor { get; } = new(); // sensor number (0 = none) → serials
    }

    private static (string Base, int? Sensor) SplitSensor(string displayName)
    {
        var m = SensorSuffix.Match(displayName);
        return m.Success
            ? (displayName[..m.Index], int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            : (displayName, null);
    }

    private static string Station(string controller)
    {
        var i = controller.IndexOf("_KF", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? controller[..i] : controller;
    }

    private static bool HasOriginColumns(string moduleRef) =>
        moduleRef.Trim().ToUpperInvariant() is "M1X" or "M2X" or "M3X";

    /// <summary>
    /// The real origin-module label for the module chart (task 1b): <c>M1x</c>→<c>M10</c>/<c>M11</c>,
    /// <c>M2x</c>→<c>M20</c>/<c>M21</c>, <c>M3x</c>→<c>M3x</c> value; a part not yet exited (no origin)
    /// becomes its own "&lt;ref&gt; (unbekannt)" bucket; NoRef and the like keep their module_ref.
    /// </summary>
    private static string OriginLabelFor(DefectPart p)
    {
        if (HasOriginColumns(p.ModuleRef))
        {
            var o = p.OriginModule?.Trim();
            if (string.IsNullOrEmpty(o))
                return $"{p.ModuleRef} (unbekannt)";
            return Regex.IsMatch(o, @"^\d+$") ? "M" + o : o;
        }
        return p.ModuleRef;
    }

    private Dictionary<string, AggGroup> AggregateGroups(IEnumerable<DefectPart> parts)
    {
        var map = new Dictionary<string, AggGroup>();
        foreach (var p in parts)
        {
            var (baseName, sensor) = SplitSensor(p.DisplayName);
            var feature = SensorsIndividual ? p.DisplayName : baseName;
            var camKey = SeparateByCamera ? p.Controller : Station(p.Controller);
            var key = camKey + "" + feature;
            if (!map.TryGetValue(key, out var g))
            {
                g = new AggGroup { CameraKey = camKey, FeatureName = feature, ModuleRef = p.ModuleRef };
                map[key] = g;
            }

            g.Serials.Add(p.Serial);
            g.Occurrences += p.Occurrences;
            g.DefIds.Add(p.DefId);

            if (!g.ByCamera.TryGetValue(p.Controller, out var camSet))
                g.ByCamera[p.Controller] = camSet = new HashSet<string>();
            camSet.Add(p.Serial);

            var sIdx = sensor ?? 0;
            if (!g.BySensor.TryGetValue(sIdx, out var senSet))
                g.BySensor[sIdx] = senSet = new HashSet<string>();
            senSet.Add(p.Serial);
        }
        return map;
    }

    /// <summary>Reset View (task C3): drop the bar-click filter + selection and restore the default
    /// aggregated view immediately (no manual Refresh needed).</summary>
    private void ResetView()
    {
        SeparateByCamera = false;
        SensorsIndividual = false;
        SelectedModuleBar = null;
        HasOriginDetail = false;
        RebuildBars(); // guarantee a rebuild even when nothing changed above
    }

    private IEnumerable<DefectPart> FilteredParts() =>
        SelectedModuleBar is null
            ? _defectParts
            : _defectParts.Where(p => string.Equals(OriginLabelFor(p), SelectedModuleBar.ModuleRef, StringComparison.OrdinalIgnoreCase));

    private void RebuildBars()
    {
        Bars.Clear();
        var groups = AggregateGroups(FilteredParts());
        var top = groups.Values
            .OrderByDescending(g => g.Serials.Count)
            .ThenByDescending(g => g.Occurrences)
            .Take(TopN)
            .ToList();
        var max = top.Count > 0 ? top.Max(g => g.Serials.Count) : 0;
        var prevGroups = AggregateGroups(_prevDefectParts); // same keying → comparable

        foreach (var g in top)
        {
            var affected = g.Serials.Count;
            var pct = _inspected > 0 ? 100.0 * affected / _inspected : 0.0;
            prevGroups.TryGetValue(g.Key, out var pg);
            var prevAffected = pg?.Serials.Count ?? 0;
            var (glyph, brush) = Trend(affected, prevAffected);

            var moduleColor = ColorFor(g.ModuleRef);
            var cams = g.ByCamera.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).ToList();
            var sumCam = cams.Sum(c => c.Value.Count);
            var segments = new List<BarSegment>();
            for (var i = 0; i < cams.Count; i++)
            {
                // Split the filled part (length = distinct affected) by each camera's share, shaded so
                // KF1/KF3 stay distinguishable (task B). Overlapping parts just weight both slices.
                var weight = sumCam > 0 ? (double)affected * cams[i].Value.Count / sumCam : affected;
                segments.Add(new BarSegment { Weight = weight, Brush = Shade(moduleColor, i, cams.Count) });
            }
            if (segments.Count == 0)
                segments.Add(new BarSegment { Weight = Math.Max(affected, 1), Brush = moduleColor });

            Bars.Add(new ParetoBarItem
            {
                Label = $"{g.CameraKey} · {DisplayFeature(g)}",
                ToolTipText = BuildTooltip(g, affected, prevAffected, cams),
                ModuleRef = g.ModuleRef,
                AffectedParts = affected,
                Occurrences = g.Occurrences,
                CountText = $"{affected}  ({pct:0.0} %)  ·  {g.Occurrences}×",
                Segments = segments,
                RemainderWeight = Math.Max(0, max - affected),
                TrendGlyph = glyph,
                TrendBrush = brush,
                DefinitionIds = g.DefIds.ToList(),
                CanShowOrigin = HasOriginColumns(g.ModuleRef),
            });
        }
    }

    private string DisplayFeature(AggGroup g)
    {
        if (SensorsIndividual)
            return g.FeatureName;
        var sensors = g.BySensor.Keys.Where(k => k > 0).OrderBy(k => k).ToList();
        if (sensors.Count == 0) return g.FeatureName;
        if (sensors.Count == 1) return $"{g.FeatureName} (S{sensors[0]})";
        return $"{g.FeatureName} (S{sensors[0]}–S{sensors[^1]})";
    }

    private string BuildTooltip(AggGroup g, int affected, int prevAffected, List<KeyValuePair<string, HashSet<string>>> cams)
    {
        var sb = new StringBuilder();
        sb.Append(g.CameraKey).Append(" · ").AppendLine(DisplayFeature(g));
        sb.Append("Module ref: ").AppendLine(g.ModuleRef);
        sb.Append("Affected parts: ").Append(affected).Append("  (previous window ").Append(prevAffected).AppendLine(")");
        sb.Append("Occurrences: ").Append(g.Occurrences).AppendLine();

        if (cams.Count > 1 || !SeparateByCamera)
        {
            sb.AppendLine("— per camera —");
            foreach (var c in cams)
                sb.Append("   ").Append(c.Key).Append(": ").Append(c.Value.Count).AppendLine();
        }

        if (!SensorsIndividual)
        {
            var sensors = g.BySensor.Where(kv => kv.Key > 0).OrderBy(kv => kv.Key).ToList();
            if (sensors.Count > 0)
            {
                sb.AppendLine("— per sensor —");
                foreach (var s in sensors)
                    sb.Append("   S").Append(s.Key).Append(": ").Append(s.Value.Count).AppendLine();
            }
        }

        if (HasOriginColumns(g.ModuleRef))
            sb.Append("(Click: origin module/nest)");
        return sb.ToString().TrimEnd();
    }

    private void RebuildModuleColors()
    {
        var modules = _defectParts.Select(a => a.ModuleRef).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        _moduleColors = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < modules.Count; i++)
            _moduleColors[modules[i]] = Frozen(Palette[i % Palette.Length]);
    }

    private Brush ColorFor(string moduleRef) =>
        _moduleColors.TryGetValue(moduleRef, out var b) ? b : Brushes.SteelBlue;

    private void RebuildModuleBars()
    {
        // Task 1b: share per REAL origin module (M10/M11, M20/M21, …) via dmcserial, with a separate
        // "<ref> (unbekannt)" segment for parts not yet exited. Coloured by the parent module_ref so it
        // matches the Top-20 bars + the legend; the click filter (below) filters on this same label.
        ModuleBars.Clear();
        var byOrigin = _defectParts
            .GroupBy(OriginLabelFor)
            .Select(grp => new
            {
                Label = grp.Key,
                ParentRef = grp.First().ModuleRef,
                Affected = grp.Select(p => p.Serial).Distinct().Count(),
            })
            .OrderBy(x => x.ParentRef, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var max = byOrigin.Count > 0 ? byOrigin.Max(x => x.Affected) : 0;
        foreach (var m in byOrigin)
        {
            ModuleBars.Add(new ModuleBarItem
            {
                ModuleRef = m.Label,
                AffectedParts = m.Affected,
                CountText = m.Affected.ToString("N0", CultureInfo.CurrentCulture),
                BarBrush = ColorFor(m.ParentRef),
                BarFraction = Fraction(m.Affected, max),
            });
        }

        Legend.Clear();
        foreach (var kv in _moduleColors.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            Legend.Add(new LegendItem(kv.Key, kv.Value));
    }

    private void RebuildWarnings(IReadOnlyList<ControllerJudge> status2)
    {
        Status2Warnings.Clear();
        foreach (var c in status2)
        {
            var text = c.Judged == 0
                ? $"⚠ {c.Controller}: camera gives no verdict — only status 2 ({c.NotJudged}× not judged)"
                : $"⚠ {c.Controller}: {c.NotJudged}× status 2 (not judged), {c.Judged}× judged";
            Status2Warnings.Add(text);
        }
        HasWarnings = Status2Warnings.Count > 0;
    }

    // --- Origin matrix (task A) --------------------------------------------

    private async Task ShowOriginAsync(ParetoBarItem? bar)
    {
        if (bar is null)
            return;

        OriginTitle = "Origin — " + bar.Label;
        OriginRows.Clear();
        OriginNests.Clear();
        HasOriginMatrix = false;
        HasOriginDetail = true;

        if (!bar.CanShowOrigin)
        {
            OriginSubtitle = string.Empty;
            OriginNote = $"No origin can be derived — module_ref = {bar.ModuleRef} (no strand relation M10/M11 or M20/M21).";
            HasOriginNote = true;
            return;
        }

        try
        {
            var cells = await _db.GetOriginAsync(_winFrom, _winTo, bar.DefinitionIds, bar.ModuleRef).ConfigureAwait(true);
            if (cells.Count == 0)
            {
                OriginSubtitle = string.Empty;
                OriginNote = "No completed parts in the time window — the origin (module/nest) is only known after part exit.";
                HasOriginNote = true;
                return;
            }

            BuildOriginMatrix(cells);
            OriginNote = string.Empty;
            HasOriginNote = false;
            OriginSubtitle =
                "Concentration on one module/nest → check mechanics/process there · " +
                "even distribution → more likely material or camera evaluation.";
        }
        catch (Exception ex)
        {
            OriginSubtitle = string.Empty;
            OriginNote = "Could not load origin: " + ex.Message;
            HasOriginNote = true;
        }
    }

    private void BuildOriginMatrix(IReadOnlyList<OriginCell> cells)
    {
        var nests = cells.Select(c => c.Nest).Distinct()
            .OrderBy(n => int.TryParse(n, out var v) ? v : int.MaxValue)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var modules = cells.Select(c => c.OriginModule).Distinct()
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var byCell = cells.ToDictionary(c => (c.OriginModule, c.Nest));

        // Elevated = clearly above the feature's overall rate, with enough samples to be trustworthy.
        var totAff = cells.Sum(c => c.Affected);
        var totIns = cells.Sum(c => c.Inspected);
        var overall = totIns > 0 ? 100.0 * totAff / totIns : 0.0;
        var threshold = Math.Max(overall * 1.5, overall + 2.0);

        foreach (var n in nests)
            OriginNests.Add("Nest " + n);

        foreach (var module in modules)
        {
            var rowCells = new List<OriginCellVm>();
            foreach (var n in nests)
            {
                if (byCell.TryGetValue((module, n), out var cell) && cell.Inspected > 0)
                {
                    var elevated = cell.Inspected >= 5 && cell.RatePct > 0 && cell.RatePct >= threshold;
                    rowCells.Add(new OriginCellVm
                    {
                        Text = $"{cell.Affected}/{cell.Inspected}",
                        RateText = cell.RatePct.ToString("0.0", CultureInfo.CurrentCulture) + " %",
                        IsEmpty = false,
                        IsElevated = elevated,
                    });
                }
                else
                {
                    rowCells.Add(new OriginCellVm { Text = "—", RateText = string.Empty, IsEmpty = true });
                }
            }

            OriginRows.Add(new OriginRowVm { Module = module, Cells = rowCells });
        }

        HasOriginMatrix = OriginRows.Count > 0;
    }

    // --- Small helpers -----------------------------------------------------

    private static (string Glyph, Brush Brush) Trend(int current, int previous)
    {
        // More affected parts is worse → up-arrow red, down-arrow green.
        if (current > previous) return ("▲", Frozen("#EF4444"));
        if (current < previous) return ("▼", Frozen("#22C55E"));
        return ("■", Frozen("#9CA3AF"));
    }

    /// <summary>Bar length as a percentage of the largest bar (0..100), for the module ProgressBars.</summary>
    private static double Fraction(int value, int max) => max <= 0 ? 0.0 : 100.0 * value / max;

    /// <summary>Lighten a base colour by camera index so a station's KF1/KF3 segments stay distinct.</summary>
    private static Brush Shade(Brush baseBrush, int index, int count)
    {
        if (baseBrush is not SolidColorBrush sb || count <= 1 || index == 0)
            return baseBrush;
        var t = (double)index / (count - 1) * 0.55;
        var c = sb.Color;
        var mixed = Color.FromRgb(
            (byte)(c.R + (255 - c.R) * t),
            (byte)(c.G + (255 - c.G) * t),
            (byte)(c.B + (255 - c.B) * t));
        var b = new SolidColorBrush(mixed);
        b.Freeze();
        return b;
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private void SetConnection(bool ok, string text)
    {
        ConnectionText = text;
        ConnectionBrush = ok ? Frozen("#22C55E") : Frozen("#EF4444");
    }

    // --- Time windows ------------------------------------------------------

    private (DateTime From, DateTime To) WindowRange(DateTime now) => SelectedWindow switch
    {
        "30 min" => (now.AddMinutes(-30), now),
        "1 h" => (now.AddHours(-1), now),
        "2 h" => (now.AddHours(-2), now),
        "4 h" => (now.AddHours(-4), now),
        "8 h" => (now.AddHours(-8), now),
        "16 h" => (now.AddHours(-16), now),
        "1 day" => (now.AddDays(-1), now),
        "2 days" => (now.AddDays(-2), now),
        "3 days" => (now.AddDays(-3), now),
        "7 days" => (now.AddDays(-7), now),
        _ => CurrentShift(now), // "Shift"
    };

    /// <summary>Current production shift window using 06:00 / 14:00 / 22:00 boundaries.</summary>
    private static (DateTime From, DateTime To) CurrentShift(DateTime now)
    {
        int[] starts = { 6, 14, 22 };
        var today = now.Date;
        DateTime start = today.AddHours(6);
        foreach (var h in starts)
        {
            var candidate = today.AddHours(h);
            if (candidate <= now) start = candidate;
        }
        if (now < today.AddHours(6))
            start = today.AddDays(-1).AddHours(22); // before the first shift → previous night shift
        return (start, now);
    }

    // --- Connection dialog / CSV / help ------------------------------------

    private void OpenConnectionDialog()
    {
        var dlg = new ConnectionDialog(_settings.Clone()) { Owner = Application.Current?.MainWindow };
        if (dlg.ShowDialog() == true)
        {
            _settings = dlg.Result;
            _settings.Save();
            _db = new ParetoDb(_settings);
            RefreshSeconds = _settings.RefreshSeconds;
            _ = AfterConnectAsync();
        }
    }

    private void ExportCsv()
    {
        if (Bars.Count == 0)
        {
            StatusMessage = "Nothing to export.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Export Pareto",
            Filter = "CSV (;) |*.csv",
            FileName = CsvExport.TimestampedName("HarryPareto"),
        };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            CsvExport.Write(
                dlg.FileName,
                new[] { "Rank", "Station_Feature", "ModuleRef", "AffectedParts", "Occurrences", "SharePercent", "Trend" },
                Bars.Select((b, i) => new[]
                {
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    b.Label,
                    b.ModuleRef,
                    b.AffectedParts.ToString(CultureInfo.InvariantCulture),
                    b.Occurrences.ToString(CultureInfo.InvariantCulture),
                    (_inspected > 0 ? 100.0 * b.AffectedParts / _inspected : 0.0).ToString("0.0", CultureInfo.InvariantCulture),
                    b.TrendGlyph,
                }));
            StatusMessage = "Exported: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            StatusMessage = "Export failed: " + ex.Message;
        }
    }

    private void ShowHelp() =>
        HelpWindow.Show(Application.Current?.MainWindow, SuiteHelp.Pareto(AppVersion));
}
