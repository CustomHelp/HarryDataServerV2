using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Data;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace HarryGraph;

/// <summary>
/// One graph panel: plots one or more selected measurements over time, each with an
/// optional time-varying Min/Max envelope. Zoom is X-only by default (Lock Y), with a
/// Reset Zoom button. Used both inline (WrapPanel) and full-screen (detail window).
/// </summary>
public partial class GraphPanelViewModel : ObservableObject
{
    /// <summary>Absolute ceiling on points fetched per series (protects the plot).</summary>
    public const int MaxPoints = 50000;

    // One colour per selected measurement (cycled); the envelope reuses the series colour.
    private static readonly OxyColor[] Palette =
    {
        OxyColor.FromRgb(0x8B, 0x5C, 0xF6), OxyColor.FromRgb(0x22, 0xC5, 0x5E),
        OxyColor.FromRgb(0xF5, 0x9E, 0x0B), OxyColor.FromRgb(0x3B, 0x82, 0xF6),
        OxyColor.FromRgb(0x14, 0xB8, 0xA6), OxyColor.FromRgb(0xEC, 0x48, 0x99),
        OxyColor.FromRgb(0xA3, 0xE6, 0x35), OxyColor.FromRgb(0xF8, 0x71, 0x71),
    };

    private readonly QueryService _query;
    private readonly Func<bool, (DateTime From, DateTime To, int Limit)> _rangeResolver;
    private readonly Action<GraphPanelViewModel>? _onMaximize;
    private readonly List<GraphDefItem> _allItems = new();
    private bool _ready;

    public GraphPanelViewModel(
        QueryService query,
        IReadOnlyList<MeasurementDefinitionRow> definitions,
        Func<bool, (DateTime From, DateTime To, int Limit)> rangeResolver,
        Action<GraphPanelViewModel>? onMaximize,
        bool isDetached = false)
    {
        _query = query;
        _rangeResolver = rangeResolver;
        _onMaximize = onMaximize;
        IsDetached = isDetached;

        foreach (var d in definitions)
            _allItems.Add(new GraphDefItem(d, OnSelectionChanged));
        ApplyFilter();

        Model = BuildModel();
        _ready = true;
    }

    public PlotModel Model { get; }

    /// <summary>True when shown in the full-screen detail window (hides the maximize button).</summary>
    public bool IsDetached { get; }
    public bool ShowMaximize => !IsDetached;

    public ObservableCollection<GraphDefItem> VisibleItems { get; } = new();

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _pickerOpen;
    [ObservableProperty] private bool _liveMode;
    [ObservableProperty] private bool _showLimits;
    [ObservableProperty] private bool _lockY = true;
    [ObservableProperty] private string _statusText = "Pick one or more measurements.";
    [ObservableProperty] private string _selectionSummary = "Select measurements ▾";

    // Set by the host to lay the panels out responsively in the WrapPanel.
    [ObservableProperty] private double _panelWidth = 620;
    [ObservableProperty] private double _panelHeight = 360;

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnLiveModeChanged(bool value) => Trigger();
    partial void OnShowLimitsChanged(bool value) => Trigger();
    partial void OnLockYChanged(bool value) => Trigger();

    private IReadOnlyList<MeasurementDefinitionRow> SelectedDefinitions =>
        _allItems.Where(i => i.IsSelected).Select(i => i.Definition).ToList();

    private void ApplyFilter()
    {
        VisibleItems.Clear();
        var filter = FilterText.Trim();
        foreach (var item in _allItems)
            if (filter.Length == 0 || item.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
                VisibleItems.Add(item);
    }

    private void OnSelectionChanged()
    {
        var count = _allItems.Count(i => i.IsSelected);
        SelectionSummary = count switch
        {
            0 => "Select measurements ▾",
            1 => _allItems.First(i => i.IsSelected).Label + " ▾",
            _ => $"{count} measurements ▾",
        };
        Trigger();
    }

    private void Trigger()
    {
        if (_ready)
            _ = RefreshAsync();
    }

    [RelayCommand] private Task Refresh() => RefreshAsync();

    [RelayCommand]
    private void ResetZoom()
    {
        Model.ResetAllAxes();
        Model.InvalidatePlot(false);
    }

    [RelayCommand]
    private void Maximize() => _onMaximize?.Invoke(this);

    public async Task RefreshAsync()
    {
        var defs = SelectedDefinitions;
        Model.Series.Clear();

        if (defs.Count == 0)
        {
            Model.Title = "Select one or more measurements";
            ApplyYAxis(double.NaN, double.NaN);
            Model.InvalidatePlot(true);
            return;
        }

        var (from, to, limit) = _rangeResolver(LiveMode);
        limit = Math.Min(limit, MaxPoints);

        try
        {
            double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
            var totalPoints = 0;
            var colorIndex = 0;

            foreach (var def in defs)
            {
                var color = Palette[colorIndex++ % Palette.Length];
                var points = await _query.GetSeriesAsync(def, from, to, limit);
                totalPoints += points.Count;

                var series = new LineSeries
                {
                    Title = def.Label,
                    Color = color,
                    StrokeThickness = 1.5,
                    MarkerType = points.Count <= 300 ? MarkerType.Circle : MarkerType.None,
                    MarkerSize = 2.5,
                    TrackerFormatString = "{0}\n{2:yyyy-MM-dd HH:mm:ss}\nValue: {4:0.000}",
                };
                foreach (var p in points)
                {
                    series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(p.MeasuredAt), p.Value));
                    yMin = Math.Min(yMin, p.Value);
                    yMax = Math.Max(yMax, p.Value);
                }
                Model.Series.Add(series);

                if (ShowLimits && points.Count > 0)
                {
                    var history = await _query.GetLimitHistoryAsync(def.CameraId, def.ParameterSet);
                    if (history.HasAny)
                    {
                        var min = NewLimitSeries($"{def.DisplayName} Min", color);
                        var max = NewLimitSeries($"{def.DisplayName} Max", color);
                        foreach (var p in points)
                        {
                            var x = DateTimeAxis.ToDouble(p.MeasuredAt);
                            if (history.MinAt(p.MeasuredAt) is { } lo) { min.Points.Add(new DataPoint(x, lo)); yMin = Math.Min(yMin, lo); }
                            if (history.MaxAt(p.MeasuredAt) is { } hi) { max.Points.Add(new DataPoint(x, hi)); yMax = Math.Max(yMax, hi); }
                        }
                        if (min.Points.Count > 0) Model.Series.Add(min);
                        if (max.Points.Count > 0) Model.Series.Add(max);
                    }
                }
            }

            ApplyYAxis(yMin, yMax);
            Model.Title = LiveMode
                ? $"Live — {defs.Count} measurement(s), {totalPoints} pts"
                : $"{defs.Count} measurement(s), {totalPoints} pts";
            StatusText = LiveMode
                ? $"Live — last {limit} per series, {totalPoints} pts"
                : $"{totalPoints} point(s), {from:yyyy-MM-dd HH:mm} → {to:yyyy-MM-dd HH:mm}";
            Model.InvalidatePlot(true);
        }
        catch (Exception ex)
        {
            StatusText = "Query failed: " + ex.Message;
        }
    }

    private static LineSeries NewLimitSeries(string title, OxyColor color) => new()
    {
        Title = title,
        Color = OxyColor.FromArgb(0xCC, color.R, color.G, color.B),
        StrokeThickness = 0.5,
        LineStyle = LineStyle.Dash,
        TrackerFormatString = "{0}\n{2:yyyy-MM-dd HH:mm:ss}\n{4:0.000}",
    };

    /// <summary>Lock Y to the data extent (X-only zoom) or release it for free zoom.</summary>
    private void ApplyYAxis(double yMin, double yMax)
    {
        var axis = Model.Axes.FirstOrDefault(a => a.Position == AxisPosition.Left);
        if (axis is null)
            return;

        if (LockY && double.IsFinite(yMin) && double.IsFinite(yMax))
        {
            var pad = Math.Max((yMax - yMin) * 0.05, 1e-6);
            axis.Minimum = yMin - pad;
            axis.Maximum = yMax + pad;
            axis.IsZoomEnabled = false;
            axis.IsPanEnabled = false;
        }
        else
        {
            axis.Minimum = double.NaN;
            axis.Maximum = double.NaN;
            axis.IsZoomEnabled = true;
            axis.IsPanEnabled = true;
        }
    }

    /// <summary>An independent copy for the detail window (PlotModel can't be shared across views).</summary>
    public GraphPanelViewModel CloneForWindow()
    {
        var selectedIds = _allItems.Where(i => i.IsSelected).Select(i => i.Definition.Id).ToHashSet();
        var defs = _allItems.Select(i => i.Definition).ToList();
        var clone = new GraphPanelViewModel(_query, defs, _rangeResolver, _onMaximize, isDetached: true) { _ready = false };
        foreach (var item in clone._allItems)
            item.IsSelected = selectedIds.Contains(item.Definition.Id);
        clone.LockY = LockY;
        clone.ShowLimits = ShowLimits;
        clone.LiveMode = LiveMode;
        clone._ready = true;
        _ = clone.RefreshAsync();
        return clone;
    }

    private static PlotModel BuildModel()
    {
        var bg = OxyColor.FromRgb(0x23, 0x27, 0x30);
        var fg = OxyColor.FromRgb(0xE5, 0xE7, 0xEB);
        var grid = OxyColor.FromArgb(0x40, 0x9C, 0xA3, 0xAF);

        var model = new PlotModel
        {
            Title = "Select one or more measurements",
            TitleFontSize = 12,
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
            LegendFontSize = 10,
        });
        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "MM-dd HH:mm",
            TitleColor = fg, TextColor = fg, TicklineColor = grid,
            MajorGridlineStyle = LineStyle.Dot, MajorGridlineColor = grid,
            IsZoomEnabled = true, IsPanEnabled = true,
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            TitleColor = fg, TextColor = fg, TicklineColor = grid,
            MajorGridlineStyle = LineStyle.Dot, MajorGridlineColor = grid,
            IsZoomEnabled = false, IsPanEnabled = false,
        });
        return model;
    }
}
