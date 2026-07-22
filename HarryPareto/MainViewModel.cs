using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Data;
using HarryShared.Help;
using Microsoft.Win32;

namespace HarryPareto;

/// <summary>One horizontal Pareto bar (a defect feature) drawn in pure XAML (task E3/E4).</summary>
public sealed class ParetoBarItem
{
    public required string Label { get; init; }
    public required string ToolTipText { get; init; }
    public required string ModuleRef { get; init; }
    public int AffectedParts { get; init; }
    public int Occurrences { get; init; }
    public required string CountText { get; init; }
    public required Brush BarBrush { get; init; }
    /// <summary>Bar length as a percentage 0..100 of the largest bar (drives a themed ProgressBar).</summary>
    public double BarFraction { get; init; }
    public required string TrendGlyph { get; init; }
    public required Brush TrendBrush { get; init; }
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

/// <summary>
/// Live Top-20 Pareto of production defect reasons. Loads read-only aggregates from
/// <see cref="ParetoDb"/>, refreshes on a timer, and exposes everything the pure-XAML view binds to
/// (KPI head, bars, module chart, legend, status-2 warnings, shift comparison). DB errors surface as
/// a status message + red connection lamp — never a crash (task E3).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int TopN = 20;

    // Categorical palette (readable in dark and light); assigned per module_ref (task E3 colour+legend).
    private static readonly string[] Palette =
    {
        "#4E79A7", "#F28E2B", "#59A14F", "#E15759", "#76B7B2",
        "#EDC948", "#B07AA1", "#FF9DA7", "#9C755F", "#BAB0AC",
    };

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
        ToggleTvCommand = new RelayCommand(() => TvMode = !TvMode);
        ShowHelpCommand = new RelayCommand(ShowHelp);
    }

    // --- Bound collections -------------------------------------------------

    public ObservableCollection<ParetoBarItem> Bars { get; } = new();
    public ObservableCollection<ModuleBarItem> ModuleBars { get; } = new();
    public ObservableCollection<LegendItem> Legend { get; } = new();
    public ObservableCollection<string> Status2Warnings { get; } = new();

    public ObservableCollection<string> WindowOptions { get; } =
        new() { "Schicht", "8 h", "24 h", "7 Tage" };
    public ObservableCollection<string> Controllers { get; } = new() { "(alle)" };

    // --- Commands ----------------------------------------------------------

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand ReconnectCommand { get; }
    public IRelayCommand ExportCsvCommand { get; }
    public IRelayCommand ClearModuleFilterCommand { get; }
    public IRelayCommand ToggleTvCommand { get; }
    public IRelayCommand ShowHelpCommand { get; }

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

    public string AppName => "HarryPareto — Live Top-20 Fehlergründe";
    public string AppVersion => "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "2.0.0");

    // --- Filters (trigger a reload) ---------------------------------------

    private string _selectedWindow = "Schicht";
    public string SelectedWindow
    {
        get => _selectedWindow;
        set { if (SetProperty(ref _selectedWindow, value)) _ = RefreshAsync(); }
    }

    private string _selectedController = "(alle)";
    public string SelectedController
    {
        get => _selectedController;
        set { if (SetProperty(ref _selectedController, value)) _ = RefreshAsync(); }
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
                ModuleFilterText = value is null ? string.Empty : $"Modul-Filter: {value.ModuleRef}";
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

    // Raw current-window aggregate cached so a module-filter click re-slices without a DB round-trip.
    private List<ParetoAgg> _agg = new();
    private Dictionary<int, int> _prevByDef = new();
    private Dictionary<string, Brush> _moduleColors = new(StringComparer.OrdinalIgnoreCase);
    private int _inspected;

    // --- Startup -----------------------------------------------------------

    /// <summary>Called once from the window: auto-connect with the saved settings, else show the dialog.</summary>
    public async Task StartupAsync()
    {
        var ok = await _db.CanConnectAsync().ConfigureAwait(true);
        if (!ok)
        {
            StatusMessage = "Auto-Connect fehlgeschlagen — bitte Verbindungsdaten prüfen.";
            SetConnection(false, "Auto-Connect fehlgeschlagen");
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
            Controllers.Add("(alle)");
            foreach (var n in names) Controllers.Add(n);
            _selectedController = Controllers.Contains(keep) ? keep : "(alle)";
            OnPropertyChanged(nameof(SelectedController));
        }
        catch (Exception ex)
        {
            StatusMessage = "Controller-Liste konnte nicht geladen werden: " + ex.Message;
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
            var ctrl = SelectedController == "(alle)" ? null : SelectedController;

            var kpi = await _db.GetKpiAsync(from, to, ctrl).ConfigureAwait(true);
            _agg = await _db.GetAggAsync(from, to, ctrl).ConfigureAwait(true);
            var prev = await _db.GetAggAsync(prevFrom, from, ctrl).ConfigureAwait(true);
            var status2 = await _db.GetStatus2Async(from, to, ctrl).ConfigureAwait(true);

            // Shift comparison (task E3): current shift vs previous shift bad-rate.
            var (csFrom, csTo) = CurrentShift(now);
            var psFrom = csFrom - (csTo - csFrom);
            var kpiShift = await _db.GetKpiAsync(csFrom, csTo, ctrl).ConfigureAwait(true);
            var kpiPrevShift = await _db.GetKpiAsync(psFrom, csFrom, ctrl).ConfigureAwait(true);

            _inspected = kpi.Inspected;
            _prevByDef = prev.ToDictionary(p => p.DefId, p => p.AffectedParts);
            RebuildModuleColors();

            InspectedText = kpi.Inspected.ToString("N0", CultureInfo.CurrentCulture);
            BadText = kpi.Bad.ToString("N0", CultureInfo.CurrentCulture);
            RateText = kpi.RatePct.ToString("0.0", CultureInfo.CurrentCulture) + " %";
            WindowText = $"{SelectedWindow}  ({from:dd.MM. HH:mm} – {to:HH:mm})";
            UpdatedText = now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture);
            ShiftComparisonText =
                $"Schicht {kpiShift.RatePct:0.0} % ({kpiShift.Bad}/{kpiShift.Inspected})   ·   " +
                $"Vorschicht {kpiPrevShift.RatePct:0.0} % ({kpiPrevShift.Bad}/{kpiPrevShift.Inspected})   ·   " +
                $"{Delta(kpiShift.RatePct - kpiPrevShift.RatePct)}";

            RebuildModuleBars();
            RebuildBars();
            RebuildWarnings(status2);

            SetConnection(true, $"verbunden — {_settings.User}@{_settings.Ip}:{_settings.Port}/{_settings.Database}");
            StatusMessage = $"Aktualisiert {now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            SetConnection(false, "Verbindungs-/Abfragefehler");
            StatusMessage = "Fehler bei der Abfrage: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private static string Delta(double d) =>
        d > 0.05 ? $"▲ +{d:0.0} %-Pkt" : d < -0.05 ? $"▼ {d:0.0} %-Pkt" : "■ ±0";

    private void RebuildModuleColors()
    {
        var modules = _agg.Select(a => a.ModuleRef).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        _moduleColors = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < modules.Count; i++)
            _moduleColors[modules[i]] = Frozen(Palette[i % Palette.Length]);
    }

    private Brush ColorFor(string moduleRef) =>
        _moduleColors.TryGetValue(moduleRef, out var b) ? b : Brushes.SteelBlue;

    private void RebuildModuleBars()
    {
        ModuleBars.Clear();
        var byModule = _agg.GroupBy(a => a.ModuleRef, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Module: g.Key, Affected: g.Sum(a => a.AffectedParts)))
            .OrderByDescending(x => x.Affected)
            .ToList();
        var max = byModule.Count > 0 ? byModule.Max(x => x.Affected) : 0;
        foreach (var m in byModule)
        {
            ModuleBars.Add(new ModuleBarItem
            {
                ModuleRef = m.Module,
                AffectedParts = m.Affected,
                CountText = m.Affected.ToString("N0", CultureInfo.CurrentCulture),
                BarBrush = ColorFor(m.Module),
                BarFraction = Fraction(m.Affected, max),
            });
        }

        Legend.Clear();
        foreach (var kv in _moduleColors.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            Legend.Add(new LegendItem(kv.Key, kv.Value));
    }

    private void RebuildBars()
    {
        Bars.Clear();
        var filtered = SelectedModuleBar is null
            ? _agg
            : _agg.Where(a => string.Equals(a.ModuleRef, SelectedModuleBar.ModuleRef, StringComparison.OrdinalIgnoreCase)).ToList();

        var top = filtered.OrderByDescending(a => a.AffectedParts).ThenByDescending(a => a.Occurrences)
            .Take(TopN).ToList();
        var max = top.Count > 0 ? top.Max(a => a.AffectedParts) : 0;

        foreach (var a in top)
        {
            var pct = _inspected > 0 ? 100.0 * a.AffectedParts / _inspected : 0.0;
            _prevByDef.TryGetValue(a.DefId, out var prevAffected);
            var (glyph, brush) = Trend(a.AffectedParts, prevAffected);

            Bars.Add(new ParetoBarItem
            {
                Label = $"{a.Controller} · {a.DisplayName}",
                ToolTipText = $"{a.Controller} · {a.DisplayName}\nModul-Ref: {a.ModuleRef}\n" +
                              $"Betroffene Teile: {a.AffectedParts}\nVorkommen: {a.Occurrences}\n" +
                              $"Vorfenster: {prevAffected}",
                ModuleRef = a.ModuleRef,
                AffectedParts = a.AffectedParts,
                Occurrences = a.Occurrences,
                CountText = $"{a.AffectedParts}  ({pct:0.0} %)  ·  {a.Occurrences}×",
                BarBrush = ColorFor(a.ModuleRef),
                BarFraction = Fraction(a.AffectedParts, max),
                TrendGlyph = glyph,
                TrendBrush = brush,
            });
        }
    }

    private void RebuildWarnings(IReadOnlyList<ControllerJudge> status2)
    {
        Status2Warnings.Clear();
        foreach (var c in status2)
        {
            var text = c.Judged == 0
                ? $"⚠ {c.Controller}: Kamera bewertet nicht — nur Status 2 ({c.NotJudged}× nicht bewertet)"
                : $"⚠ {c.Controller}: {c.NotJudged}× Status 2 (nicht bewertet), {c.Judged}× bewertet";
            Status2Warnings.Add(text);
        }
        HasWarnings = Status2Warnings.Count > 0;
    }

    private static (string Glyph, Brush Brush) Trend(int current, int previous)
    {
        // More affected parts is worse → up-arrow red, down-arrow green.
        if (current > previous) return ("▲", Frozen("#EF4444"));
        if (current < previous) return ("▼", Frozen("#22C55E"));
        return ("■", Frozen("#9CA3AF"));
    }

    /// <summary>Bar length as a percentage of the largest bar (0..100), for the ProgressBar fill.</summary>
    private static double Fraction(int value, int max) => max <= 0 ? 0.0 : 100.0 * value / max;

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
        "8 h" => (now.AddHours(-8), now),
        "24 h" => (now.AddHours(-24), now),
        "7 Tage" => (now.AddDays(-7), now),
        _ => CurrentShift(now), // "Schicht"
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
            StatusMessage = "Nichts zu exportieren.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Pareto exportieren",
            Filter = "CSV (;) |*.csv",
            FileName = CsvExport.TimestampedName("HarryPareto"),
        };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            CsvExport.Write(
                dlg.FileName,
                new[] { "Rang", "Controller_Merkmal", "ModulRef", "BetroffeneTeile", "Vorkommen", "AnteilProzent", "Trend" },
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
            StatusMessage = "Exportiert: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            StatusMessage = "Export fehlgeschlagen: " + ex.Message;
        }
    }

    private void ShowHelp() =>
        HelpWindow.Show(Application.Current?.MainWindow, SuiteHelp.Pareto(AppVersion));
}
