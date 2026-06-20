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

        FromDate = DateTime.Today;
        ToDate = DateTime.Today;

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
    [ObservableProperty] private DateTime _toDate;
    [ObservableProperty] private int _liveWindowMinutes = 30;
    [ObservableProperty] private string _statusMessage = "Loading measurement list …";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPanelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemovePanelCommand))]
    private int _panelCount;

    private async Task LoadDefinitionsAsync()
    {
        try
        {
            _definitions = await _query.GetActiveDefinitionsAsync();
            StatusMessage = $"{_definitions.Count} active measurements. Pick one per graph.";
            if (Panels.Count == 0)
                AddPanel(); // start with one graph window
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to load measurement list: " + ex.Message;
        }
    }

    /// <summary>Range for a panel: rolling window when live, the shared from/to otherwise.</summary>
    private (DateTime From, DateTime To) ResolveRange(bool isLive)
    {
        if (isLive)
        {
            var now = DateTime.Now;
            return (now.AddMinutes(-Math.Max(1, LiveWindowMinutes)), now);
        }
        return (FromDate.Date, ToDate.Date.AddDays(1).AddSeconds(-1));
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
