using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>One part (DMC) of a run in the MSA UI — its per-part verdict + ok/total counter (task B).</summary>
public sealed class MsaPartRow
{
    public required string Dmc { get; init; }
    public required MsaVerdict Verdict { get; init; }
    public required int OkCount { get; init; }
    public required int TotalCount { get; init; }
    public string MatchedReference { get; init; } = string.Empty;

    public string DmcText => string.IsNullOrEmpty(Dmc) ? "(Study — all parts)" : Dmc;
    public string VerdictText => Verdict switch
    {
        MsaVerdict.Pass => "PASS",
        MsaVerdict.Fail => "FAIL",
        _ => "INVALID",
    };
    public Brush VerdictBrush => Verdict switch
    {
        MsaVerdict.Pass => Brushes.SeaGreen,
        MsaVerdict.Fail => Brushes.IndianRed,
        _ => Brushes.DarkOrange,
    };
    public string CountText => $"{OkCount}/{TotalCount} ok";
}

/// <summary>
/// Paginated view over the stored MSA runs of one module and one MSA type. Shows one run at a time;
/// within a run a list of parts (DMCs) with per-part verdict, and for the selected part its measurements
/// (task B). Buttons act on the SELECTED PART: open its two PDFs and its report folder.
/// </summary>
public sealed partial class MsaRunsViewModel : ObservableObject
{
    private readonly IMsaService _msa;
    private readonly IPdfReportService _pdf;
    private readonly string _module;
    private readonly MsaType _type;

    private List<MsaRunDto> _runs = new();
    private int _index = -1;

    public MsaRunsViewModel(IMsaService msa, string module, MsaType type, IPdfReportService pdf)
    {
        _msa = msa;
        _pdf = pdf;
        _module = module;
        _type = type;

        PreviousCommand = new RelayCommand(() => Move(-1), () => _index > 0);
        NextCommand = new RelayCommand(() => Move(+1), () => _index >= 0 && _index < _runs.Count - 1);
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        OpenAllResultsCommand = new RelayCommand(() => OpenReport(failuresOnly: false), () => SelectedPart is not null);
        OpenFailuresOnlyCommand = new RelayCommand(() => OpenReport(failuresOnly: true), () => SelectedPart is not null);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => SelectedPart is not null);
    }

    public ObservableCollection<MsaPartRow> Parts { get; } = new();
    public ObservableCollection<MsaResultRow> Features { get; } = new();

    public IRelayCommand PreviousCommand { get; }
    public IRelayCommand NextCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand OpenAllResultsCommand { get; }
    public IRelayCommand OpenFailuresOnlyCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }

    [ObservableProperty] private string _runInfo = "No runs loaded.";
    [ObservableProperty] private string _overallText = "—";
    [ObservableProperty] private Brush _overallBrush = Brushes.Gray;
    [ObservableProperty] private bool _hasRuns;

    private MsaPartRow? _selectedPart;
    public MsaPartRow? SelectedPart
    {
        get => _selectedPart;
        set
        {
            if (SetProperty(ref _selectedPart, value))
            {
                UpdateFeatures();
                OpenAllResultsCommand.NotifyCanExecuteChanged();
                OpenFailuresOnlyCommand.NotifyCanExecuteChanged();
                OpenFolderCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private MsaRunDto? Current => _index >= 0 && _index < _runs.Count ? _runs[_index] : null;

    public async Task LoadAsync()
    {
        var runs = await _msa.GetRunsAsync(_module, _type).ConfigureAwait(true);
        _runs = runs.ToList();
        _index = _runs.Count - 1; // newest
        UpdateCurrent();
    }

    private void Move(int delta)
    {
        _index = Math.Clamp(_index + delta, 0, Math.Max(0, _runs.Count - 1));
        UpdateCurrent();
    }

    private void UpdateCurrent()
    {
        Parts.Clear();
        Features.Clear();
        var run = Current;
        HasRuns = run is not null;

        if (run is null)
        {
            RunInfo = "No runs available.";
            OverallText = "—";
            OverallBrush = Brushes.Gray;
        }
        else
        {
            BuildParts(run);
            var (verdict, _) = MsaReportData.ComputeVerdict(_type, run.Rows, wholeRun: true);
            RunInfo = $"Run {_index + 1} of {_runs.Count}  ·  {run.EvaluatedAt:yyyy-MM-dd HH:mm:ss}  ·  BaseID {run.BaseId}";
            OverallText = verdict switch { MsaVerdict.Pass => "PASS", MsaVerdict.Fail => "FAIL", _ => "INVALID" };
            OverallBrush = verdict switch
            {
                MsaVerdict.Pass => Brushes.SeaGreen,
                MsaVerdict.Fail => Brushes.IndianRed,
                _ => Brushes.DarkOrange,
            };
        }

        SelectedPart = Parts.Count > 0 ? Parts[0] : null;

        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        OpenAllResultsCommand.NotifyCanExecuteChanged();
        OpenFailuresOnlyCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    private void BuildParts(MsaRunDto run)
    {
        if (_type == MsaType.Msa3)
        {
            // MSA3 is one study over all parts — a single synthetic "part".
            var (verdict, _) = MsaReportData.ComputeVerdict(_type, run.Rows, wholeRun: true);
            var ev = run.Rows.Where(r => r.Evaluated).ToList();
            Parts.Add(new MsaPartRow
            {
                Dmc = string.Empty, Verdict = verdict,
                OkCount = ev.Count(r => r.Passed), TotalCount = ev.Count,
            });
            return;
        }

        foreach (var g in run.Rows.GroupBy(r => r.Dmc, StringComparer.OrdinalIgnoreCase))
        {
            var partRows = g.ToList();
            var verdict = MsaReportData.ComputeVerdict(_type, partRows, wholeRun: false).Verdict;
            var ev = partRows.Where(r => r.Evaluated).ToList();
            Parts.Add(new MsaPartRow
            {
                Dmc = g.Key,
                Verdict = verdict,
                OkCount = ev.Count(r => r.Passed),
                TotalCount = ev.Count,
                MatchedReference = partRows.Select(r => r.MatchedReference).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? string.Empty,
            });
        }
    }

    private void UpdateFeatures()
    {
        Features.Clear();
        var run = Current;
        var part = SelectedPart;
        if (run is null || part is null)
            return;

        var rows = _type == MsaType.Msa3 || string.IsNullOrEmpty(part.Dmc)
            ? run.Rows
            : run.Rows.Where(r => string.Equals(r.Dmc, part.Dmc, StringComparison.OrdinalIgnoreCase));
        foreach (var r in rows)
            Features.Add(r);
    }

    /// <summary>Open the selected part's AllResults / FailuresOnly PDF (generate on demand if missing).</summary>
    private void OpenReport(bool failuresOnly)
    {
        var run = Current;
        var part = SelectedPart;
        if (run is null || part is null)
            return;

        try
        {
            var report = _type == MsaType.Msa3 || string.IsNullOrEmpty(part.Dmc)
                ? MsaReportData.FromRun(run)
                : MsaReportData.ForPart(run, part.Dmc);
            var paths = _pdf.ResolvePaths(report);
            var path = failuresOnly ? paths.FailuresOnly : paths.AllResults;

            if (!File.Exists(path))
            {
                paths = _pdf.Generate(report);
                path = failuresOnly ? paths.FailuresOnly : paths.AllResults;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the PDF report:\n\n{ex.Message}",
                "MSA Report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Open the run's report folder (&lt;ReportPath&gt;\&lt;Date&gt;\&lt;Module&gt;\&lt;BaseID&gt;) in Explorer.</summary>
    private void OpenFolder()
    {
        var run = Current;
        var part = SelectedPart;
        if (run is null || part is null)
            return;

        try
        {
            var report = _type == MsaType.Msa3 || string.IsNullOrEmpty(part.Dmc)
                ? MsaReportData.FromRun(run)
                : MsaReportData.ForPart(run, part.Dmc);
            var pdfPath = _pdf.ResolvePaths(report).AllResults;         // …\<BaseID>\PDF\<file>.pdf
            var runRoot = Path.GetDirectoryName(Path.GetDirectoryName(pdfPath)); // …\<BaseID>
            if (string.IsNullOrEmpty(runRoot))
                return;
            Directory.CreateDirectory(runRoot);
            Process.Start(new ProcessStartInfo(runRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the report folder:\n\n{ex.Message}",
                "MSA Report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
