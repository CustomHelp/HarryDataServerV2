using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Config;
using HarryShared.Data;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace HarryCounter;

/// <summary>One way to group the NG counts (drives both the query and the labels).</summary>
public sealed record GroupingOption(string Name, string Kind, string? Column = null)
{
    public override string ToString() => Name;
}

/// <summary>
/// HarryCounter view model: counts NG parts/measurements over a time range, grouped by
/// error category (feature_group), measurement, or nest/module. Shows a results grid +
/// bar chart and a yield summary, with an optional 5 s live refresh.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const int MaxBars = 25;

    private readonly QueryService _query;
    private readonly HarryConfig _config;
    private readonly DispatcherTimer _liveTimer;

    public MainViewModel(QueryService query, HarryConfig config)
    {
        _query = query;
        _config = config;
        ConfigFile = config.IniPath;

        FromDate = DateTime.Today;
        ToDate = DateTime.Today;
        SelectedGrouping = Groupings[0];

        Model = BuildEmptyModel();

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _liveTimer.Tick += async (_, _) => await RefreshAsync();

        _ = RefreshAsync();
    }

    public string AppName => "HarryCounter — NG Error Counter";
    public string AppVersion => "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "2.0.0");
    public string ConfigFile { get; }

    public IReadOnlyList<GroupingOption> Groupings { get; } = new[]
    {
        new GroupingOption("Error category (Feature Group)", "feature"),
        new GroupingOption("Measurement", "measurement"),
        new GroupingOption("M50 Nest", "nest", "m50_nest"),
        new GroupingOption("M1x Nest", "nest", "m1x_nest"),
        new GroupingOption("M3x Nest", "nest", "m3x_nest"),
        new GroupingOption("M1x Module", "nest", "m1x_module"),
        new GroupingOption("M3x Module", "nest", "m3x_module"),
        new GroupingOption("Order", "nest", "order_name"),
    };

    public ObservableCollection<CountRow> Results { get; } = new();
    public PlotModel Model { get; }

    [ObservableProperty] private GroupingOption _selectedGrouping;
    [ObservableProperty] private DateTime _fromDate;
    [ObservableProperty] private DateTime _toDate;
    [ObservableProperty] private bool _liveMode;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _statusMessage = "Choose a range and grouping, then Refresh.";

    partial void OnSelectedGroupingChanged(GroupingOption value) => _ = RefreshAsync();

    partial void OnLiveModeChanged(bool value)
    {
        if (value) _liveTimer.Start(); else _liveTimer.Stop();
        _ = RefreshAsync();
    }

    [RelayCommand] private void RangeToday() { FromDate = DateTime.Today; ToDate = DateTime.Today; _ = RefreshAsync(); }
    [RelayCommand] private void Range7Days() { FromDate = DateTime.Today.AddDays(-6); ToDate = DateTime.Today; _ = RefreshAsync(); }
    [RelayCommand] private void Range30Days() { FromDate = DateTime.Today.AddDays(-29); ToDate = DateTime.Today; _ = RefreshAsync(); }

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    private async Task RefreshAsync()
    {
        var from = FromDate.Date;
        var to = ToDate.Date.AddDays(1).AddSeconds(-1);
        var grouping = SelectedGrouping;

        try
        {
            var rows = grouping.Kind switch
            {
                "feature" => await _query.GetNgByFeatureGroupAsync(from, to),
                "measurement" => await _query.GetNgByMeasurementAsync(from, to),
                "nest" => await _query.GetNgByNestAsync(grouping.Column!, from, to),
                _ => new List<CountRow>(),
            };

            Results.Clear();
            foreach (var row in rows)
                Results.Add(row);

            var total = await _query.GetTotalPartCountAsync(from, to);
            var ng = await _query.GetNgPartCountAsync(from, to);
            var yield = total > 0 ? 100.0 * (total - ng) / total : 0.0;
            Summary = $"Parts: {total}   NG parts: {ng}   Yield: {yield:0.00}%   ({grouping.Name}: {rows.Count} groups, {rows.Sum(r => r.Count)} NG measurements)";

            UpdateChart(rows, grouping.Name);
            StatusMessage = $"Updated {DateTime.Now:HH:mm:ss} — {from:yyyy-MM-dd} to {to:yyyy-MM-dd}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Query failed: " + ex.Message;
        }
    }

    private void UpdateChart(List<CountRow> rows, string title)
    {
        Model.Series.Clear();
        Model.Axes.Clear();

        var top = rows.Take(MaxBars).ToList();
        var fg = OxyColor.FromRgb(0xE5, 0xE7, 0xEB);
        var grid = OxyColor.FromArgb(0x40, 0x9C, 0xA3, 0xAF);

        var category = new CategoryAxis
        {
            Position = AxisPosition.Left,
            TextColor = fg, TitleColor = fg, TicklineColor = grid,
        };
        // Chart reads top-to-bottom: reverse so the largest bar is on top.
        foreach (var r in Enumerable.Reverse(top))
            category.Labels.Add(r.GroupKey);
        Model.Axes.Add(category);

        Model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            MinimumPadding = 0, AbsoluteMinimum = 0,
            TextColor = fg, TitleColor = fg, TicklineColor = grid,
            MajorGridlineStyle = LineStyle.Dot, MajorGridlineColor = grid,
            Title = "NG count",
        });

        var series = new BarSeries
        {
            FillColor = OxyColor.FromRgb(0x8B, 0x5C, 0xF6),
            LabelPlacement = LabelPlacement.Outside,
            LabelFormatString = "{0}",
            TextColor = fg,
        };
        foreach (var r in Enumerable.Reverse(top))
            series.Items.Add(new BarItem { Value = r.Count });
        Model.Series.Add(series);

        Model.Title = $"Top {top.Count} — {title}";
        Model.InvalidatePlot(true);
    }

    [RelayCommand]
    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = CsvExport.TimestampedName("HarryCounter"),
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var header = new[] { SelectedGrouping.Name, "NG Count" };
            var rows = Results.Select(r => new string?[] { r.GroupKey, r.Count.ToString() });
            CsvExport.Write(dialog.FileName, header, rows);
            StatusMessage = $"Exported {Results.Count} rows.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Export failed: " + ex.Message;
        }
    }

    private static PlotModel BuildEmptyModel()
    {
        var bg = OxyColor.FromRgb(0x23, 0x27, 0x30);
        var fg = OxyColor.FromRgb(0xE5, 0xE7, 0xEB);
        return new PlotModel
        {
            Title = "NG counts",
            Background = bg,
            PlotAreaBackground = OxyColor.FromRgb(0x1A, 0x1D, 0x23),
            TextColor = fg, TitleColor = fg,
            PlotAreaBorderColor = OxyColor.FromArgb(0x40, 0x9C, 0xA3, 0xAF),
        };
    }
}
