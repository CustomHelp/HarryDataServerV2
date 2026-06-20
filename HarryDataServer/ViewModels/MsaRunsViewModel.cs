using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>
/// Paginated view over the stored MSA runs of one module and one MSA type. Loads from
/// <c>msa_results</c> on demand, shows one run at a time, and supports Previous/Next
/// navigation plus a CSV export of the current run.
/// </summary>
public sealed partial class MsaRunsViewModel : ObservableObject
{
    private readonly IMsaService _msa;
    private readonly string _module;
    private readonly MsaType _type;

    private List<MsaRunDto> _runs = new();
    private int _index = -1;

    public MsaRunsViewModel(IMsaService msa, string module, MsaType type)
    {
        _msa = msa;
        _module = module;
        _type = type;

        PreviousCommand = new RelayCommand(() => Move(-1), () => _index > 0);
        NextCommand = new RelayCommand(() => Move(+1), () => _index >= 0 && _index < _runs.Count - 1);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => Current is not null);
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
    }

    public ObservableCollection<MsaResultRow> Rows { get; } = new();

    public IRelayCommand PreviousCommand { get; }
    public IRelayCommand NextCommand { get; }
    public IRelayCommand ExportCsvCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }

    [ObservableProperty] private string _runInfo = "No runs loaded.";
    [ObservableProperty] private string _overallText = "—";
    [ObservableProperty] private Brush _overallBrush = Brushes.Gray;
    [ObservableProperty] private bool _hasRuns;

    private MsaRunDto? Current => _index >= 0 && _index < _runs.Count ? _runs[_index] : null;

    /// <summary>Load all runs for this module/type and show the newest.</summary>
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
        Rows.Clear();
        var run = Current;
        HasRuns = run is not null;

        if (run is null)
        {
            RunInfo = "No runs available.";
            OverallText = "—";
            OverallBrush = Led.Gray;
        }
        else
        {
            foreach (var row in run.Rows)
                Rows.Add(row);

            RunInfo = $"Run {_index + 1} of {_runs.Count}  ·  {run.EvaluatedAt:yyyy-MM-dd HH:mm:ss}  ·  {run.Controller}  ·  BaseID {run.BaseId}";
            OverallText = run.OverallPass ? "PASS" : "FAIL";
            OverallBrush = run.OverallPass ? Led.Green : Led.Red;
        }

        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    private void ExportCsv()
    {
        var run = Current;
        if (run is null)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"MSA_{_type}_{_module}_{run.EvaluatedAt:yyyyMMdd_HHmmss}.csv",
        };
        if (dialog.ShowDialog() != true)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("MeasurementName;Cg;Cgk;PctTolerance;Expected;Actual;Passed");
        foreach (var r in run.Rows)
        {
            sb.Append(r.DisplayName).Append(';')
              .Append(Fmt(r.Cg)).Append(';')
              .Append(Fmt(r.Cgk)).Append(';')
              .Append(Fmt(r.PctTolerance)).Append(';')
              .Append(r.Expected ?? string.Empty).Append(';')
              .Append(r.Actual ?? string.Empty).Append(';')
              .Append(r.Passed ? "PASS" : "FAIL")
              .AppendLine();
        }
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Fmt(double? v) =>
        v?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
}
