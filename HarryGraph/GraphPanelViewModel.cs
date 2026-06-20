using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Data;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace HarryGraph;

/// <summary>
/// One graph panel: plots a single measurement's value over time with an optional
/// time-varying Min/Max envelope. Zoom is X-only by default (Lock Y), with a Reset
/// Zoom button. Used both inline (in the WrapPanel) and full-screen (maximized window).
/// </summary>
public partial class GraphPanelViewModel : ObservableObject
{
    private const int MaxPoints = 50000;

    private static readonly OxyColor SeriesColor = OxyColor.FromRgb(0x8B, 0x5C, 0xF6);
    private static readonly OxyColor LimitColor = OxyColor.FromRgb(0xEF, 0x44, 0x44);

    private readonly QueryService _query;
    private readonly Func<bool, (DateTime From, DateTime To)> _rangeResolver;
    private readonly Action<GraphPanelViewModel>? _onMaximize;
    private bool _ready;

    public GraphPanelViewModel(
        QueryService query,
        IReadOnlyList<MeasurementDefinitionRow> definitions,
        Func<bool, (DateTime From, DateTime To)> rangeResolver,
        Action<GraphPanelViewModel>? onMaximize)
    {
        _query = query;
        Definitions = definitions;
        _rangeResolver = rangeResolver;
        _onMaximize = onMaximize;
        Model = BuildModel();
        _ready = true;
    }

    public IReadOnlyList<MeasurementDefinitionRow> Definitions { get; }
    public PlotModel Model { get; }

    [ObservableProperty] private MeasurementDefinitionRow? _selectedDefinition;
    [ObservableProperty] private bool _liveMode;
    [ObservableProperty] private bool _showLimits;
    [ObservableProperty] private bool _lockY = true;
    [ObservableProperty] private string _statusText = "Select a measurement.";

    // Set by the host to lay the panels out responsively in the WrapPanel.
    [ObservableProperty] private double _panelWidth = 620;
    [ObservableProperty] private double _panelHeight = 360;

    partial void OnSelectedDefinitionChanged(MeasurementDefinitionRow? value) => Trigger();
    partial void OnLiveModeChanged(bool value) => Trigger();
    partial void OnShowLimitsChanged(bool value) => Trigger();
    partial void OnLockYChanged(bool value) => Trigger();

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
        var def = SelectedDefinition;
        Model.Series.Clear();

        if (def is null)
        {
            Model.Title = "Select a measurement";
            ApplyYAxis(double.NaN, double.NaN);
            Model.InvalidatePlot(true);
            return;
        }

        var (from, to) = _rangeResolver(LiveMode);

        try
        {
            var points = await _query.GetSeriesAsync(def, from, to, MaxPoints);

            var series = new LineSeries
            {
                Title = def.DisplayName,
                Color = SeriesColor,
                StrokeThickness = 1.5,
                MarkerType = points.Count <= 300 ? MarkerType.Circle : MarkerType.None,
                MarkerSize = 2.5,
                TrackerFormatString = "{0}\n{2:yyyy-MM-dd HH:mm:ss}\nValue: {4:0.000}",
            };
            double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
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
                    var min = NewLimitSeries("Min");
                    var max = NewLimitSeries("Max");
                    foreach (var p in points)
                    {
                        var x = DateTimeAxis.ToDouble(p.MeasuredAt);
                        var mn = history.MinAt(p.MeasuredAt);
                        var mx = history.MaxAt(p.MeasuredAt);
                        if (mn is { } lo) { min.Points.Add(new DataPoint(x, lo)); yMin = Math.Min(yMin, lo); }
                        if (mx is { } hi) { max.Points.Add(new DataPoint(x, hi)); yMax = Math.Max(yMax, hi); }
                    }
                    if (min.Points.Count > 0) Model.Series.Add(min);
                    if (max.Points.Count > 0) Model.Series.Add(max);
                }
            }

            ApplyYAxis(yMin, yMax);

            Model.Title = LiveMode
                ? $"{def.Label}  (live, {points.Count} pts)"
                : $"{def.Label}  ({points.Count} pts)";
            StatusText = $"{points.Count} point(s), {from:yyyy-MM-dd} → {to:yyyy-MM-dd}";
            Model.InvalidatePlot(true);
        }
        catch (Exception ex)
        {
            StatusText = "Query failed: " + ex.Message;
        }
    }

    private static LineSeries NewLimitSeries(string title) => new()
    {
        Title = title,
        Color = LimitColor,
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

    /// <summary>An independent copy for the maximized window (PlotModel can't be shared across views).</summary>
    public GraphPanelViewModel CloneForWindow()
    {
        var clone = new GraphPanelViewModel(_query, Definitions, _rangeResolver, _onMaximize) { _ready = false };
        clone.LockY = LockY;
        clone.ShowLimits = ShowLimits;
        clone.LiveMode = LiveMode;
        clone.SelectedDefinition = SelectedDefinition;
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
            Title = "Select a measurement",
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
