using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Config;
using HarryShared.Data;
using Microsoft.Win32;

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

    public MainViewModel(QueryService query, HarryConfig config)
    {
        _query = query;
        _config = config;
        ConfigFile = config.IniPath;

        FromDate = DateTime.Today;
        ToDate = DateTime.Today;

        _level1 = Dimensions[1]; // Feature Group
        _level2 = Dimensions[2]; // Measurement
        _level3 = Dimensions[5]; // M50 Nest

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _liveTimer.Tick += async (_, _) => await RefreshAsync();

        _ = RefreshAsync();
    }

    public string AppName => "HarryCounter — NG Error Counter";
    public string AppVersion => "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "2.0.0");
    public string ConfigFile { get; }

    public IReadOnlyList<GroupDimension> Dimensions { get; } = new[]
    {
        new GroupDimension("(none)", null),
        new GroupDimension("Feature Group", r => r.FeatureGroup),
        new GroupDimension("Measurement", r => r.Measurement),
        new GroupDimension("M1x Nest", r => r.M1xNest ?? "(none)"),
        new GroupDimension("M3x Nest", r => r.M3xNest ?? "(none)"),
        new GroupDimension("M50 Nest", r => r.M50Nest ?? "(none)"),
    };

    public ObservableCollection<ErrorTreeNode> Tree { get; } = new();

    [ObservableProperty] private GroupDimension _level1;
    [ObservableProperty] private GroupDimension _level2;
    [ObservableProperty] private GroupDimension _level3;
    [ObservableProperty] private DateTime _fromDate;
    [ObservableProperty] private DateTime _toDate;
    [ObservableProperty] private bool _liveMode;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _statusMessage = "Choose a range and grouping, then Refresh.";

    partial void OnLevel1Changed(GroupDimension value) => BuildTree();
    partial void OnLevel2Changed(GroupDimension value) => BuildTree();
    partial void OnLevel3Changed(GroupDimension value) => BuildTree();

    partial void OnLiveModeChanged(bool value)
    {
        if (value) _liveTimer.Start(); else _liveTimer.Stop();
        _ = RefreshAsync();
    }

    [RelayCommand] private void RangeToday() { FromDate = DateTime.Today; ToDate = DateTime.Today; _ = RefreshAsync(); }
    [RelayCommand] private void Range7Days() { FromDate = DateTime.Today.AddDays(-6); ToDate = DateTime.Today; _ = RefreshAsync(); }
    [RelayCommand] private void Range30Days() { FromDate = DateTime.Today.AddDays(-29); ToDate = DateTime.Today; _ = RefreshAsync(); }

    [RelayCommand] private Task Refresh() => RefreshAsync();

    private async Task RefreshAsync()
    {
        var from = FromDate.Date;
        var to = ToDate.Date.AddDays(1).AddSeconds(-1);
        try
        {
            _rows = await _query.GetErrorTreeRowsAsync(from, to);

            var totalParts = await _query.GetTotalPartCountAsync(from, to);
            var ngParts = await _query.GetNgPartCountAsync(from, to);
            var yield = totalParts > 0 ? 100.0 * (totalParts - ngParts) / totalParts : 0.0;
            var ngMeas = _rows.Where(r => r.ResultStatus == 0).Sum(r => r.Count);
            Summary = $"Parts: {totalParts}   NG parts: {ngParts}   Yield: {yield:0.00}%   NG measurements: {ngMeas}";

            BuildTree();
            StatusMessage = $"Updated {DateTime.Now:HH:mm:ss} — {from:yyyy-MM-dd} to {to:yyyy-MM-dd}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Query failed: " + ex.Message;
        }
    }

    private List<GroupDimension> ActiveDimensions =>
        new[] { Level1, Level2, Level3 }.Where(d => d is { IsNone: false }).ToList();

    private void BuildTree()
    {
        Tree.Clear();
        // First level expanded so the structure is visible at a glance.
        foreach (var node in BuildNodes(_rows, ActiveDimensions, 0, expandThisLevel: true))
            Tree.Add(node);
    }

    /// <summary>Recursively group rows; the deepest level appends OK/NG result leaves.</summary>
    private static List<ErrorTreeNode> BuildNodes(
        IReadOnlyList<ErrorAggRow> rows, List<GroupDimension> dims, int index, bool expandThisLevel)
    {
        if (index >= dims.Count)
            return ResultLeaves(rows);

        var dim = dims[index];
        var nodes = new List<ErrorTreeNode>();
        foreach (var group in rows.GroupBy(dim.KeyOf!))
        {
            var ng = group.Where(r => r.ResultStatus == 0).Sum(r => r.Count);
            var node = new ErrorTreeNode(group.Key, ng, NodeKind.Group, expanded: expandThisLevel);
            foreach (var child in BuildNodes(group.ToList(), dims, index + 1, expandThisLevel: false))
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
