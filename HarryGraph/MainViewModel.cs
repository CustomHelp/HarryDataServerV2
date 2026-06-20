using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Config;
using HarryShared.Data;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace HarryGraph;

/// <summary>
/// HarryGraph view model: pick one or more active measurement definitions and plot
/// their values over time. Two modes — Live (1 s auto-refresh, rolling window) and
/// Fixed range (from/to dates). The selection + range can be saved/loaded as JSON.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const int MaxPoints = 50000;

    private readonly QueryService _query;
    private readonly HarryConfig _config;
    private readonly DispatcherTimer _liveTimer;
    private readonly List<DefItem> _allDefs = new();
    private bool _loading;

    // OxyPlot colour palette (cycled per series).
    private static readonly OxyColor[] Palette =
    {
        OxyColor.FromRgb(0x8B, 0x5C, 0xF6), OxyColor.FromRgb(0x22, 0xC5, 0x5E),
        OxyColor.FromRgb(0xF5, 0x9E, 0x0B), OxyColor.FromRgb(0x3B, 0x82, 0xF6),
        OxyColor.FromRgb(0xEF, 0x44, 0x44), OxyColor.FromRgb(0x14, 0xB8, 0xA6),
        OxyColor.FromRgb(0xEC, 0x48, 0x99), OxyColor.FromRgb(0xA3, 0xE6, 0x35),
    };

    public MainViewModel(QueryService query, HarryConfig config)
    {
        _query = query;
        _config = config;
        ConfigFile = config.IniPath;

        FromDate = DateTime.Today;
        ToDate = DateTime.Today;

        Model = BuildEmptyModel();

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTimer.Tick += async (_, _) => await RefreshAsync();

        _ = LoadDefinitionsAsync();
    }

    public string AppName => "HarryGraph — Measurement Trend";
    public string AppVersion => "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "2.0.0");
    public string ConfigFile { get; }

    public ObservableCollection<DefItem> Definitions { get; } = new();
    public PlotModel Model { get; }

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _liveMode;
    [ObservableProperty] private int _liveWindowMinutes = 30;
    [ObservableProperty] private DateTime _fromDate;
    [ObservableProperty] private DateTime _toDate;
    [ObservableProperty] private string _statusMessage = "Loading measurement list …";

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnLiveModeChanged(bool value)
    {
        if (value)
        {
            _liveTimer.Start();
            StatusMessage = "Live mode — refreshing every second.";
        }
        else
        {
            _liveTimer.Stop();
        }
        _ = RefreshAsync();
    }

    private async Task LoadDefinitionsAsync()
    {
        try
        {
            var defs = await _query.GetActiveDefinitionsAsync();
            _allDefs.Clear();
            foreach (var d in defs)
                _allDefs.Add(new DefItem(d, OnSelectionChanged));
            ApplyFilter();
            StatusMessage = $"{_allDefs.Count} active measurements. Select one or more to plot.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to load measurement list: " + ex.Message;
        }
    }

    private void ApplyFilter()
    {
        Definitions.Clear();
        var filter = FilterText.Trim();
        foreach (var d in _allDefs)
        {
            if (filter.Length == 0 || d.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Definitions.Add(d);
        }
    }

    private void OnSelectionChanged()
    {
        if (!_loading)
            _ = RefreshAsync();
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    private async Task RefreshAsync()
    {
        var selected = _allDefs.Where(d => d.IsSelected).ToList();

        DateTime from, to;
        if (LiveMode)
        {
            to = DateTime.Now;
            from = to.AddMinutes(-Math.Max(1, LiveWindowMinutes));
        }
        else
        {
            from = FromDate.Date;
            to = ToDate.Date.AddDays(1).AddSeconds(-1);
        }

        Model.Series.Clear();

        if (selected.Count == 0)
        {
            Model.Title = "Select one or more measurements";
            Model.InvalidatePlot(true);
            return;
        }

        try
        {
            var total = 0;
            var colorIndex = 0;
            foreach (var item in selected)
            {
                var points = await _query.GetSeriesAsync(item.Definition, from, to, MaxPoints);
                var series = new LineSeries
                {
                    Title = item.Label,
                    Color = Palette[colorIndex++ % Palette.Length],
                    StrokeThickness = 1.5,
                    MarkerType = points.Count <= 300 ? MarkerType.Circle : MarkerType.None,
                    MarkerSize = 2.5,
                };
                foreach (var p in points)
                    series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(p.MeasuredAt), p.Value));
                Model.Series.Add(series);
                total += points.Count;
            }

            Model.Title = LiveMode
                ? $"Live — last {LiveWindowMinutes} min ({total} points)"
                : $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd} ({total} points)";

            if (!LiveMode)
                StatusMessage = $"Plotted {total} point(s) across {selected.Count} measurement(s).";

            Model.ResetAllAxes();
            Model.InvalidatePlot(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Query failed: " + ex.Message;
        }
    }

    private PlotModel BuildEmptyModel()
    {
        var bg = OxyColor.FromRgb(0x23, 0x27, 0x30);
        var fg = OxyColor.FromRgb(0xE5, 0xE7, 0xEB);
        var grid = OxyColor.FromArgb(0x40, 0x9C, 0xA3, 0xAF);

        var model = new PlotModel
        {
            Title = "Select one or more measurements",
            Background = bg,
            PlotAreaBackground = OxyColor.FromRgb(0x1A, 0x1D, 0x23),
            TextColor = fg,
            TitleColor = fg,
            PlotAreaBorderColor = grid,
        };
        model.Legends.Add(new OxyPlot.Legends.Legend
        {
            LegendTextColor = fg,
            LegendPosition = OxyPlot.Legends.LegendPosition.TopRight,
        });
        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "MM-dd HH:mm",
            TitleColor = fg,
            TextColor = fg,
            TicklineColor = grid,
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = grid,
            Title = "Time",
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            TitleColor = fg,
            TextColor = fg,
            TicklineColor = grid,
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = grid,
            Title = "Value",
        });
        return model;
    }

    // ===== Save / load graph config ========================================

    private sealed record GraphConfig(
        bool LiveMode, int LiveWindowMinutes, DateTime FromDate, DateTime ToDate, List<int> DefinitionIds);

    [RelayCommand]
    private void SaveConfig()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Graph config (*.json)|*.json",
            FileName = "graph-config.json",
        };
        if (dialog.ShowDialog() != true)
            return;

        var cfg = new GraphConfig(
            LiveMode, LiveWindowMinutes, FromDate, ToDate,
            _allDefs.Where(d => d.IsSelected).Select(d => d.Id).ToList());

        try
        {
            File.WriteAllText(dialog.FileName,
                JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            StatusMessage = $"Saved config ({cfg.DefinitionIds.Count} measurement(s)).";
        }
        catch (Exception ex)
        {
            StatusMessage = "Save failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task LoadConfig()
    {
        var dialog = new OpenFileDialog { Filter = "Graph config (*.json)|*.json" };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var cfg = JsonSerializer.Deserialize<GraphConfig>(File.ReadAllText(dialog.FileName));
            if (cfg is null)
                return;

            _loading = true;
            var wanted = cfg.DefinitionIds.ToHashSet();
            foreach (var d in _allDefs)
                d.IsSelected = wanted.Contains(d.Id);

            LiveWindowMinutes = cfg.LiveWindowMinutes;
            FromDate = cfg.FromDate;
            ToDate = cfg.ToDate;
            LiveMode = cfg.LiveMode;
            _loading = false;

            await RefreshAsync();
            StatusMessage = $"Loaded config ({wanted.Count} measurement(s)).";
        }
        catch (Exception ex)
        {
            _loading = false;
            StatusMessage = "Load failed: " + ex.Message;
        }
    }
}
