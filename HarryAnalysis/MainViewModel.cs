using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Communication;
using HarryShared.Config;
using HarryShared.Data;
using HarryShared.Help;
using HarryShared.Theming;
using Microsoft.Win32;

namespace HarryAnalysis;

/// <summary>
/// Drives the scanner view: the operator scans a DMC, SZID or VirtualSerial; the tool
/// loads the part header and every measurement, keeps the last 20 scans in a history
/// list (top), and shows the selected scan's detail (bottom). The measurement grid can
/// be sorted by clicking column headers and exported to CSV.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const int MaxHistory = 20;
    private const string AppKey = "HarryAnalysis";

    private readonly QueryService _query;
    private readonly HarryConfig _config;
    private readonly ScannerCompanionClient _scanner;

    public MainViewModel(QueryService query, HarryConfig config, ScannerCompanionClient scanner)
    {
        _query = query;
        _config = config;
        _scanner = scanner;
        ConfigFile = config.IniPath;

        // Scanner bridge: remember the operator's last Active/Inactive choice; subscribe to the
        // rebroadcast client. Codes are received regardless of the toggle, but only forwarded to the
        // search when Active (see OnScannerCode). The LED reflects the connection, not the toggle.
        ScannerActive = ScannerToggleState.Load(AppKey);
        _scanner.CodeReceived += OnScannerCode;
        _scanner.ConnectionChanged += OnScannerConnectionChanged;
    }

    public string AppName => "HarryAnalysis — Part Inspector";
    public string AppVersion => "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "2.0.0");

    /// <summary>Open the shared bilingual help window (also on F1).</summary>
    [RelayCommand]
    private void ShowHelp() =>
        HelpWindow.Show(System.Windows.Application.Current?.MainWindow, SuiteHelp.Analysis(AppVersion));
    public string ConfigFile { get; }

    /// <summary>Last 20 scans, newest first (in-memory only).</summary>
    public ObservableCollection<ScanHistoryEntry> History { get; } = new();

    /// <summary>Measurements of the currently selected history entry.</summary>
    public ObservableCollection<PartMeasurementRow> Measurements { get; } = new();

    [ObservableProperty] private string _scanText = string.Empty;
    [ObservableProperty] private string _statusMessage = "Scan a DMC, SZID or VirtualSerial, then press Enter.";

    // --- Scanner bridge ---
    /// <summary>When false, scans from the handheld scanner are received but ignored (persisted).</summary>
    [ObservableProperty] private bool _scannerActive = true;

    /// <summary>Connection LED to the server's companion broadcast port (independent of the toggle).</summary>
    [ObservableProperty] private Brush _scannerLed = LedBrushes.Gray;

    [ObservableProperty] private string _scannerStatus = "Scanner: connecting…";

    partial void OnScannerActiveChanged(bool value) => ScannerToggleState.Save(AppKey, value);

    /// <summary>A code arrived from the scanner: if Active, replay it as manual entry + Enter.</summary>
    private void OnScannerCode(string code)
    {
        if (!ScannerActive)
            return; // received-but-ignored scans are silently dropped (no logger in the companion tools)

        Post(() =>
        {
            ScanText = code;
            SearchCommand.ExecuteAsync(null);
        });
    }

    private void OnScannerConnectionChanged(bool connected)
    {
        Post(() =>
        {
            ScannerLed = connected ? LedBrushes.Green : LedBrushes.Red;
            ScannerStatus = connected
                ? $"Scanner: connected ({_scanner.Endpoint})"
                : $"Scanner: disconnected ({_scanner.Endpoint})";
        });
    }

    /// <summary>Marshal onto the UI thread (scanner events fire on a background socket thread).</summary>
    private static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private ScanHistoryEntry? _selectedEntry;

    public PartInfo? Part => SelectedEntry?.Part;

    partial void OnSelectedEntryChanged(ScanHistoryEntry? value)
    {
        Measurements.Clear();
        if (value is not null)
            foreach (var m in value.Measurements)
                Measurements.Add(m);

        OnPropertyChanged(nameof(Part));
        OnPropertyChanged(nameof(HeaderInfo));
        OnPropertyChanged(nameof(MatchedFieldText));
        OnPropertyChanged(nameof(ResultBadge));
    }

    /// <summary>Header card text for the selected part.</summary>
    public string HeaderInfo
    {
        get
        {
            if (SelectedEntry is { Found: false })
                return $"No part found in the database for '{SelectedEntry.Scan}'.";
            if (Part is null)
                return "No part selected.";
            var p = Part;
            return string.Join("    ",
                $"DMC: {p.Dmc ?? "-"}",
                $"SZID: {p.SerialNumber}",
                $"VirtualSerial: {p.SerialTrimmer ?? "-"}",
                $"Order: {p.OrderName ?? "-"}",
                $"Temperature: {p.M1xTemperature?.ToString("0.0") ?? "-"}",
                $"Humidity: {p.M1xHumidity?.ToString("0.0") ?? "-"}",
                $"M1X: mod {p.M1xModule?.ToString() ?? "-"} / nest {p.M1xNest?.ToString() ?? "-"}",
                $"M2X: mod {p.M2xModule?.ToString() ?? "-"} / nest {p.M2xNest?.ToString() ?? "-"}",
                $"M3X: {p.M3xModule ?? "-"} / nest {p.M3xNest ?? "-"}",
                $"M50 nest: {p.M50Nest ?? "-"}",
                $"Created: {p.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        }
    }

    public string MatchedFieldText => SelectedEntry is null ? string.Empty : $"matched on {SelectedEntry.MatchedField}";
    public string ResultBadge => Part?.ResultText ?? string.Empty;

    [RelayCommand]
    private async Task SearchAsync()
    {
        var scan = ScanText.Trim();
        if (scan.Length == 0)
        {
            StatusMessage = "Enter a DMC, SZID or VirtualSerial first.";
            return;
        }

        StatusMessage = $"Searching for '{scan}' …";
        try
        {
            // Resolve via dmcserial when a finished-part record exists, otherwise directly from the
            // measurement tables (camera data before the PLC part-exit is still inspectable).
            var part = await _query.FindPartForInspectionAsync(scan);
            if (part is null)
            {
                // Keep the miss visible in the list (not just the status label): add a red
                // "NICHT GEFUNDEN" row for the scanned value and select it.
                var miss = new ScanHistoryEntry(DateTime.Now, scan);
                History.Insert(0, miss);
                while (History.Count > MaxHistory)
                    History.RemoveAt(History.Count - 1);
                SelectedEntry = miss;
                ScanText = string.Empty;
                StatusMessage = $"No part found for '{scan}'.";
                return;
            }

            var measurements = await _query.GetPartMeasurementsAsync(part);
            var matched = MatchedField(scan, part);
            var entry = new ScanHistoryEntry(DateTime.Now, scan, matched, part, measurements);

            History.Insert(0, entry);
            while (History.Count > MaxHistory)
                History.RemoveAt(History.Count - 1);

            SelectedEntry = entry;
            ScanText = string.Empty;

            var ng = measurements.Count(m => m.ResultStatus == 0);
            StatusMessage = $"Loaded {measurements.Count} measurement(s) — {ng} NG — {matched}. Result: {part.ResultText}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Query failed: " + ex.Message;
        }
    }

    /// <summary>Which dmcserial field the scan value matched (for the result header).</summary>
    private static string MatchedField(string scan, PartInfo part)
    {
        if (string.Equals(scan, part.Dmc, StringComparison.OrdinalIgnoreCase)) return "DMC";
        if (string.Equals(scan, part.SerialNumber, StringComparison.OrdinalIgnoreCase)) return "SZID";
        if (string.Equals(scan, part.SerialTrimmer, StringComparison.OrdinalIgnoreCase)) return "VirtualSerial";
        return "DMC/SZID/VirtualSerial";
    }

    [RelayCommand]
    private void ClearAll()
    {
        History.Clear();
        SelectedEntry = null;
        StatusMessage = "History cleared.";
    }

    private bool HasSelection => SelectedEntry is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelectedEntry()
    {
        if (SelectedEntry is null)
            return;
        var idx = History.IndexOf(SelectedEntry);
        History.Remove(SelectedEntry);
        SelectedEntry = History.Count == 0 ? null : History[Math.Min(idx, History.Count - 1)];
        StatusMessage = $"Removed entry. {History.Count} in history.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Export()
    {
        if (SelectedEntry?.Part is not { } part)
        {
            StatusMessage = "Nothing to export for a not-found scan.";
            return;
        }

        var defaultDir = Directory.Exists(_config.CsvBasePath) ? _config.CsvBasePath : string.Empty;
        var dmc = string.IsNullOrWhiteSpace(part.Dmc) ? part.SerialNumber : part.Dmc!;
        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"Analysis_{Sanitize(dmc)}_{DateTime.Now:ddMMyy_HHmmss}.csv",
            InitialDirectory = defaultDir,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var header = new[] { "Measurement", "Camera", "Module", "FeatureGroup", "Value", "Min", "Max", "Result", "MeasuredAt" };
            var rows = SelectedEntry.Measurements.Select(m => new string?[]
            {
                m.DisplayName, m.CameraName, m.Module, m.FeatureGroup,
                m.ValueText, m.MinText, m.MaxText, m.ResultText,
                m.MeasuredAt.ToString("yyyy-MM-dd HH:mm:ss"),
            });
            CsvExport.Write(dialog.FileName, header, rows);
            StatusMessage = $"Exported {SelectedEntry.Measurements.Count} rows to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Export failed: " + ex.Message;
            MessageBox.Show(ex.Message, "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ExportAll()
    {
        if (History.Count == 0)
        {
            StatusMessage = "Nothing to export — the history is empty.";
            return;
        }

        var defaultDir = Directory.Exists(_config.CsvBasePath) ? _config.CsvBasePath : string.Empty;
        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"Analysis_All_{DateTime.Now:ddMMyy_HHmmss}.csv",
            InitialDirectory = defaultDir,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            // One CSV with every scanned part's measurements; leading columns identify the part.
            var header = new[]
            {
                "Scanned", "DMC", "SZID", "VirtualSerial", "PartResult",
                "Measurement", "Camera", "Module", "FeatureGroup", "Value", "Min", "Max", "Result", "MeasuredAt",
            };
            var rows = History.SelectMany(entry => entry.Measurements.Select(m => new string?[]
            {
                entry.TimestampText,
                entry.Part.Dmc ?? "-",
                entry.Part.SerialNumber,
                entry.Part.SerialTrimmer ?? "-",
                entry.Part.ResultText,
                m.DisplayName, m.CameraName, m.Module, m.FeatureGroup,
                m.ValueText, m.MinText, m.MaxText, m.ResultText,
                m.MeasuredAt.ToString("yyyy-MM-dd HH:mm:ss"),
            }));
            CsvExport.Write(dialog.FileName, header, rows);
            var total = History.Sum(h => h.Measurements.Count);
            StatusMessage = $"Exported {History.Count} part(s) / {total} measurement(s) to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Export failed: " + ex.Message;
            MessageBox.Show(ex.Message, "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
}
