using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Config;
using HarryShared.Data;
using HarryShared.Help;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace HarryCounter;

/// <summary>A grouping dimension for the error tree, with how to read its key from a row.</summary>
public sealed record GroupDimension(string Name, Func<ErrorAggRow, string>? KeyOf)
{
    public override string ToString() => Name;
    public bool IsNone => KeyOf is null;
}

/// <summary>
/// HarryCounter view model: builds a multi-level NG error tree over a time range. The user
/// chooses the grouping order via three ComboBoxes (e.g. Feature → Measurement → Nest); a
/// result breakdown (OK/NG leaves) is appended at the bottom. Includes a yield summary,
/// optional 5 s live refresh, and CSV export of the flattened groups.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    // Separator used only internally to build a composite GroupBy key for export.
    private const char KeySep = (char)31;

    private readonly QueryService _query;
    private readonly HarryConfig _config;
    private readonly DispatcherTimer _liveTimer;
    private List<ErrorAggRow> _rows = new();
    private bool _treeBuilt;

    public MainViewModel(QueryService query, HarryConfig config)
    {
        _query = query;
        _config = config;
        ConfigFile = config.IniPath;

        var now = DateTime.Now;
        FromDate = DateTime.Today;
        FromTime = TimeSpan.Zero;     // from = today 00:00
        ToDate = DateTime.Today;
        ToTime = now.TimeOfDay;       // to = now

        _level1 = Dimensions[1]; // Feature Group
        _level2 = Dimensions[2]; // Measurement
        _level3 = Dimensions[5]; // M50 Nest

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _liveTimer.Tick += async (_, _) => await RefreshAsync();

        _ = RefreshAsync();
    }

    public string AppName => "HarryCounter — NG Error Counter";
    public string AppVersion => "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "2.0.0");

    /// <summary>Open the shared bilingual help window (also on F1).</summary>
    [RelayCommand]
    private void ShowHelp() =>
        HelpWindow.Show(System.Windows.Application.Current?.MainWindow, SuiteHelp.Counter(AppVersion));
    public string ConfigFile { get; }

    public IReadOnlyList<GroupDimension> Dimensions { get; } = new[]
    {
        new GroupDimension("(none)", null),
        new GroupDimension("Feature Group", r => r.FeatureGroup),
        new GroupDimension("Measurement", r => r.Measurement),
        new GroupDimension("M1x Nest", r => r.M1xNest ?? "(none)"),
        new GroupDimension("M2x Nest", r => r.M2xNest ?? "(none)"),
        new GroupDimension("M3x Nest", r => r.M3xNest ?? "(none)"),
        new GroupDimension("M50 Nest", r => r.M50Nest ?? "(none)"),
    };

    public ObservableCollection<ErrorTreeNode> Tree { get; } = new();
    public PlotModel Model { get; } = BuildEmptyChart();

    [ObservableProperty] private GroupDimension _level1;
    [ObservableProperty] private GroupDimension _level2;
    [ObservableProperty] private GroupDimension _level3;
    [ObservableProperty] private DateTime _fromDate;
    [ObservableProperty] private TimeSpan _fromTime;
    [ObservableProperty] private DateTime _toDate;
    [ObservableProperty] private TimeSpan _toTime;
    [ObservableProperty] private bool _liveMode;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _statusMessage = "Choose a range and grouping, then Refresh.";

    // Live view: aggregate over the most recent N finished parts (editable combo).
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

    partial void OnLiveCountTextChanged(string value)
    {
        if (LiveMode) _ = RefreshAsync();
    }

    // A grouping change restructures the tree, so it returns to the default state (top level expanded).
    partial void OnLevel1Changed(GroupDimension value) => BuildTree(applyDefault: true);
    partial void OnLevel2Changed(GroupDimension value) => BuildTree(applyDefault: true);
    partial void OnLevel3Changed(GroupDimension value) => BuildTree(applyDefault: true);

    partial void OnLiveModeChanged(bool value)
    {
        if (value) _liveTimer.Start(); else _liveTimer.Stop();
        _ = RefreshAsync();
    }

    // The day-range presets cover whole days (00:00:00 → 23:59:59); narrow the time fields to reduce volume.
    private static readonly TimeSpan DayStart = TimeSpan.Zero;
    private static readonly TimeSpan DayEnd = new(23, 59, 59);

    [RelayCommand] private void RangeToday() { FromDate = DateTime.Today; FromTime = DayStart; ToDate = DateTime.Today; ToTime = DayEnd; _ = RefreshAsync(); }
    [RelayCommand] private void Range7Days() { FromDate = DateTime.Today.AddDays(-6); FromTime = DayStart; ToDate = DateTime.Today; ToTime = DayEnd; _ = RefreshAsync(); }
    [RelayCommand] private void Range30Days() { FromDate = DateTime.Today.AddDays(-29); FromTime = DayStart; ToDate = DateTime.Today; ToTime = DayEnd; _ = RefreshAsync(); }

    [RelayCommand] private Task Refresh() => RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            int totalParts, ngParts;
            string scope;

            if (LiveMode)
            {
                // Live: aggregate over the most recent N finished parts (LIMIT N at the query level).
                var n = LiveCount;
                _rows = await _query.GetErrorTreeRowsLastNAsync(n);
                (totalParts, ngParts) = await _query.GetPartStatsLastNAsync(n);
                scope = $"last {n} parts";
            }
            else
            {
                // Range: full date+time bounds (narrow the time to cut data volume).
                var from = FromDate.Date + FromTime;
                var to = ToDate.Date + ToTime;
                _rows = await _query.GetErrorTreeRowsAsync(from, to);
                totalParts = await _query.GetTotalPartCountAsync(from, to);
                ngParts = await _query.GetNgPartCountAsync(from, to);
                scope = $"{from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm}";
            }

            var yield = totalParts > 0 ? 100.0 * (totalParts - ngParts) / totalParts : 0.0;
            var ngMeas = _rows.Where(r => r.ResultStatus == 0).Sum(r => r.Count);
            Summary = $"Parts: {totalParts}   NG parts: {ngParts}   Yield: {yield:0.00}%   NG measurements: {ngMeas}";

            // First build uses the default expansion; later refreshes preserve the user's state.
            BuildTree(applyDefault: !_treeBuilt);
            StatusMessage = $"Updated {DateTime.Now:HH:mm:ss} — {scope}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Query failed: " + ex.Message;
        }
    }

    private List<GroupDimension> ActiveDimensions =>
        new[] { Level1, Level2, Level3 }.Where(d => d is { IsNone: false }).ToList();

    /// <summary>Collapse the tree back to the default state (top level expanded) on demand.</summary>
    [RelayCommand] private void ResetTree() => BuildTree(applyDefault: true);

    /// <summary>
    /// Rebuild the tree from the current rows. The source collection is replaced (TreeViewItems are
    /// regenerated), so the user's expand/collapse + selection is captured by a stable path key and
    /// re-applied — unless <paramref name="applyDefault"/> (first build / grouping change / Reset),
    /// which restores the default state (top-level groups expanded, the rest collapsed). Nodes that
    /// only appear after a refresh are not in the captured set, so they default to collapsed.
    /// </summary>
    private void BuildTree(bool applyDefault)
    {
        HashSet<string> expanded = new();
        string? selected = null;
        if (!applyDefault)
            CaptureState(Tree, string.Empty, expanded, ref selected);

        Tree.Clear();
        foreach (var node in BuildNodes(_rows, ActiveDimensions, 0))
            Tree.Add(node);

        if (applyDefault)
        {
            // Default: top-level groups expanded so the structure is visible at a glance.
            foreach (var node in Tree)
                node.IsExpanded = true;
        }
        else
        {
            RestoreState(Tree, string.Empty, expanded, selected);
        }

        _treeBuilt = true;
        BuildChart();
    }

    /// <summary>Collect the path-keys of expanded nodes + the selected node's path (stable across rebuilds).</summary>
    private static void CaptureState(
        IEnumerable<ErrorTreeNode> nodes, string parentPath, HashSet<string> expanded, ref string? selected)
    {
        foreach (var n in nodes)
        {
            var path = parentPath.Length == 0 ? n.Key : parentPath + KeySep + n.Key;
            if (n.IsExpanded) expanded.Add(path);
            if (n.IsSelected) selected = path;
            CaptureState(n.Children, path, expanded, ref selected);
        }
    }

    /// <summary>Re-apply captured expansion/selection to the rebuilt tree; unseen nodes stay collapsed.</summary>
    private static void RestoreState(
        IEnumerable<ErrorTreeNode> nodes, string parentPath, HashSet<string> expanded, string? selected)
    {
        foreach (var n in nodes)
        {
            var path = parentPath.Length == 0 ? n.Key : parentPath + KeySep + n.Key;
            if (expanded.Contains(path)) n.IsExpanded = true;
            if (selected is not null && selected == path) n.IsSelected = true;
            RestoreState(n.Children, path, expanded, selected);
        }
    }

    /// <summary>Bar chart of the top-level groups' NG counts (the first chosen dimension).</summary>
    private void BuildChart()
    {
        Model.Series.Clear();
        Model.Axes.Clear();

        var fg = OxyColor.FromRgb(0xE5, 0xE7, 0xEB);
        var grid = OxyColor.FromArgb(0x40, 0x9C, 0xA3, 0xAF);
        var top = Tree.Take(25).ToList();
        var firstDim = ActiveDimensions.FirstOrDefault()?.Name ?? "Result";

        var category = new CategoryAxis
        {
            Position = AxisPosition.Left,
            TextColor = fg, TitleColor = fg, TicklineColor = grid,
        };
        // Largest bar on top: reverse for the bottom-up category axis.
        foreach (var n in Enumerable.Reverse(top))
            category.Labels.Add(n.Key);
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
        foreach (var n in Enumerable.Reverse(top))
            series.Items.Add(new BarItem { Value = n.Count });
        Model.Series.Add(series);

        Model.Title = $"Top {top.Count} — {firstDim} (NG)";
        Model.InvalidatePlot(true);
    }

    private static PlotModel BuildEmptyChart()
    {
        var fg = OxyColor.FromRgb(0xE5, 0xE7, 0xEB);
        return new PlotModel
        {
            Title = "NG counts",
            Background = OxyColor.FromRgb(0x23, 0x27, 0x30),
            PlotAreaBackground = OxyColor.FromRgb(0x1A, 0x1D, 0x23),
            TextColor = fg, TitleColor = fg,
            PlotAreaBorderColor = OxyColor.FromArgb(0x40, 0x9C, 0xA3, 0xAF),
        };
    }

    /// <summary>Recursively group rows; the deepest level appends OK/NG result leaves. Nodes are
    /// created collapsed — expansion is applied afterwards by <see cref="BuildTree"/>.</summary>
    private static List<ErrorTreeNode> BuildNodes(
        IReadOnlyList<ErrorAggRow> rows, List<GroupDimension> dims, int index)
    {
        if (index >= dims.Count)
            return ResultLeaves(rows);

        var dim = dims[index];
        var nodes = new List<ErrorTreeNode>();
        foreach (var group in rows.GroupBy(dim.KeyOf!))
        {
            var ng = group.Where(r => r.ResultStatus == 0).Sum(r => r.Count);
            var node = new ErrorTreeNode(group.Key, ng, NodeKind.Group);
            foreach (var child in BuildNodes(group.ToList(), dims, index + 1))
                node.Children.Add(child);
            nodes.Add(node);
        }
        return nodes.OrderByDescending(n => n.Count).ToList();
    }

    private static List<ErrorTreeNode> ResultLeaves(IReadOnlyList<ErrorAggRow> rows)
    {
        var ok = rows.Where(r => r.ResultStatus == 1).Sum(r => r.Count);
        var ng = rows.Where(r => r.ResultStatus == 0).Sum(r => r.Count);
        var leaves = new List<ErrorTreeNode>();
        if (ng > 0) leaves.Add(new ErrorTreeNode("NG", ng, NodeKind.Ng));
        if (ok > 0) leaves.Add(new ErrorTreeNode("OK", ok, NodeKind.Ok));
        return leaves;
    }

    [RelayCommand]
    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = CsvExport.TimestampedName("HarryCounter"),
            InitialDirectory = System.IO.Directory.Exists(_config.CsvBasePath) ? _config.CsvBasePath : string.Empty,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var dims = ActiveDimensions;
            var header = dims.Select(d => d.Name).Append("OK").Append("NG").ToList();

            // Flatten: one row per unique combination of the active dimension keys.
            var grouped = _rows
                .GroupBy(r => string.Join(KeySep, dims.Select(d => d.KeyOf!(r))))
                .Select(g =>
                {
                    var keys = g.Key.Split(KeySep);
                    var ok = g.Where(r => r.ResultStatus == 1).Sum(r => r.Count);
                    var ng = g.Where(r => r.ResultStatus == 0).Sum(r => r.Count);
                    return (IEnumerable<string?>)keys.Append(ok.ToString()).Append(ng.ToString()).ToList();
                })
                .ToList();

            CsvExport.Write(dialog.FileName, header, grouped);
            StatusMessage = $"Exported {grouped.Count} group row(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = "Export failed: " + ex.Message;
        }
    }
}
