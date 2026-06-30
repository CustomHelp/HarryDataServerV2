using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Config;
using HarryShared.Data;

namespace HarryGraph;

/// <summary>
/// HarryGraph host: shows 1–6 graph panels in a responsive WrapPanel, each plotting its
/// own measurement. A shared from/to range drives non-live panels; a single 1 s timer
/// refreshes the live ones. Panels can be added/removed or opened full-screen.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public const int MaxPanels = 6;

    private readonly QueryService _query;
    private readonly DispatcherTimer _liveTimer;
    private IReadOnlyList<MeasurementDefinitionRow> _definitions = Array.Empty<MeasurementDefinitionRow>();

    /// <summary>Raised when a panel asks to be opened in a full-screen window (handled by the view).</summary>
    public event Action<GraphPanelViewModel>? MaximizeRequested;

    public MainViewModel(QueryService query, HarryConfig config)
    {
        _query = query;
        ConfigFile = config.IniPath;

        var now = DateTime.Now;
        FromDate = DateTime.Today;
        FromTime = TimeSpan.Zero;          // from = today 00:00
        ToDate = DateTime.Today;
        ToTime = now.TimeOfDay;            // to = now

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTimer.Tick += async (_, _) => await TickLiveAsync();
        _liveTimer.Start();

        _ = LoadDefinitionsAsync();
    }

    public string AppName => "HarryGraph — Measurement Trend";
    public string AppVersion => "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "2.0.0");
    public string ConfigFile { get; }

    public ObservableCollection<GraphPanelViewModel> Panels { get; } = new();

    [ObservableProperty] private DateTime _fromDate;
    [ObservableProperty] private TimeSpan _fromTime;
    [ObservableProperty] private DateTime _toDate;
    [ObservableProperty] private TimeSpan _toTime;
    [ObservableProperty] private string _statusMessage = "Loading measurement list …";

    // Live view: how many of the most recent points per series to show (editable combo).
    public IReadOnlyList<int> LiveCountPresets { get; } = LiveView.Presets;
    [ObservableProperty] private string _liveCountText = LiveView.DefaultCount.ToString();
    private int _lastValidLiveCount = LiveView.DefaultCount;

    /// <summary>Validated last-N count; remembers the last valid value on bad input.</summary>
    public int LiveCount
    {
        get
        {
            _lastValidLiveCount = LiveView.ParseCount(LiveCountText, _lastValidLiveCount);
            return _lastValidLiveCount;
        }
    }

    partial void OnLiveCountTextChanged(string value) => _ = TickLiveAsync();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPanelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemovePanelCommand))]
    private int _panelCount;

    private async Task LoadDefinitionsAsync()
    {
        try
        {
            // Only the Result (R_) definitions: each measurement appears once, and the float
            // measurement_value lives on the Result-keyed row (the Value defs have no rows of their
            // own — see MeasurementRowBuilder), so this lists each trend once and still plots the value.
            _definitions = await _query.GetActiveDefinitionsAsync("Result");
            StatusMessage = $"{_definitions.Count} active measurements. Pick one per graph.";
            if (Panels.Count == 0)
                AddPanel(); // start with one graph window
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to load measurement list: " + ex.Message;
        }
    }

    /// <summary>
    /// Range + row limit for a panel. Live: the most recent <see cref="LiveCount"/> points (no
    /// lower time bound). Non-live: the shared from/to date+time range, capped to a generous limit.
    /// </summary>
    private (DateTime From, DateTime To, int Limit) ResolveRange(bool isLive)
    {
        if (isLive)
            return (new DateTime(2000, 1, 1), DateTime.Now, LiveCount);

        var from = FromDate.Date + FromTime;
        var to = ToDate.Date + ToTime;
        return (from, to, GraphPanelViewModel.MaxPoints);
    }

    private bool CanAddPanel => Panels.Count < MaxPanels;
    private bool CanRemovePanel => Panels.Count > 1;

    [RelayCommand(CanExecute = nameof(CanAddPanel))]
    private void AddPanel()
    {
        var panel = new GraphPanelViewModel(_query, _definitions, ResolveRange,
            p => MaximizeRequested?.Invoke(p));
        Panels.Add(panel);
        PanelCount = Panels.Count;
    }

    [RelayCommand(CanExecute = nameof(CanRemovePanel))]
    private void RemovePanel()
    {
        if (Panels.Count == 0)
            return;
        Panels.RemoveAt(Panels.Count - 1);
        PanelCount = Panels.Count;
    }

    [RelayCommand]
    private async Task RefreshAll()
    {
        foreach (var panel in Panels)
            await panel.RefreshAsync();
        StatusMessage = $"Refreshed {Panels.Count} graph(s) at {DateTime.Now:HH:mm:ss}.";
    }

    private async Task TickLiveAsync()
    {
        foreach (var panel in Panels)
            if (panel.LiveMode)
                await panel.RefreshAsync();
    }
}
