using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Communication;
using HarryShared.Config;
using HarryShared.Data;
using HarryShared.Theming;

namespace HarryLimitSample;

/// <summary>
/// HarryLimitSample editor: scan a part DMC, load its measurements, mark each as
/// should-pass / should-fail / ignore, and save a per-module LimitSample reference
/// (MSA_&lt;module&gt;.json — the limit_sample_expected map keyed by display name) that
/// the MSA engine uses to evaluate LimitSample runs. Existing references can be
/// loaded to view/edit/delete entries; the references (xm) block is preserved on save.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const string AppKey = "HarryLimitSample";

    private readonly QueryService _query;
    private readonly HarryConfig _config;
    private readonly ScannerCompanionClient _scanner;
    private readonly List<LimitSampleRow> _allRows = new();

    public MainViewModel(QueryService query, HarryConfig config, ScannerCompanionClient scanner)
    {
        _query = query;
        _config = config;
        _scanner = scanner;
        ConfigFile = config.IniPath;
        ReferenceFolder = string.IsNullOrWhiteSpace(config.MsaReferencePath)
            ? "(not set in Harry.ini [MSA] ReferencePath)"
            : config.MsaReferencePath;

        // Scanner bridge: remember the operator's last Active/Inactive choice; subscribe to the
        // rebroadcast client. Codes are received regardless of the toggle, but only forwarded to the
        // search when Active. The LED reflects the connection, not the toggle.
        ScannerActive = ScannerToggleState.Load(AppKey);
        _scanner.CodeReceived += OnScannerCode;
        _scanner.ConnectionChanged += OnScannerConnectionChanged;
    }

    public string AppName => "HarryLimitSample — Reference Editor";
    public string AppVersion => "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "2.0.0");
    public string ConfigFile { get; }
    public string ReferenceFolder { get; }

    /// <summary>Full path the current module's reference file will be written to.</summary>
    public string SavePathPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_config.MsaReferencePath))
                return "Saving to: (set [MSA] ReferencePath in Harry.ini)";
            var module = ResolveTargetModule();
            if (module is null)
                return "Saving to: select a single module to see the path";
            return "Saving to: " + Path.Combine(_config.MsaReferencePath, MsaReferenceFile.FileName(module));
        }
    }

    public ObservableCollection<LimitSampleRow> Rows { get; } = new();
    public ObservableCollection<string> Modules { get; } = new();
    public Array Expectations => Enum.GetValues(typeof(Expectation));

    [ObservableProperty] private string _scanText = string.Empty;
    [ObservableProperty] private string _msaVersion = "v1";
    [ObservableProperty] private string _statusMessage = "Scan a part DMC to load its measurements.";

    /// <summary>True when the last search found no part — shows a red banner above the grid.</summary>
    [ObservableProperty] private bool _notFound;
    [ObservableProperty] private string _notFoundText = string.Empty;

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

    [ObservableProperty] private string? _selectedModule;

    partial void OnSelectedModuleChanged(string? value)
    {
        ApplyModuleFilter();
        OnPropertyChanged(nameof(SavePathPreview));
    }

    private void ApplyModuleFilter()
    {
        Rows.Clear();
        foreach (var r in _allRows)
        {
            if (SelectedModule is null || SelectedModule == "(all)" || r.Module == SelectedModule)
                Rows.Add(r);
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var scan = ScanText.Trim();
        if (scan.Length == 0)
        {
            StatusMessage = "Enter a DMC or serial number first.";
            return;
        }

        StatusMessage = $"Searching for '{scan}' …";
        NotFound = false;
        _allRows.Clear();
        Rows.Clear();
        Modules.Clear();

        try
        {
            // Resolve via dmcserial when a finished-part record exists, otherwise directly from the
            // measurement tables (camera data before the PLC part-exit is still inspectable).
            var part = await _query.FindPartForInspectionAsync(scan);
            if (part is null)
            {
                NotFound = true;
                NotFoundText = $"Kein Teil in der Datenbank gefunden zu: {scan}";
                StatusMessage = $"No part found for '{scan}'.";
                return;
            }

            var measurements = await _query.GetPartMeasurementsAsync(part);
            foreach (var m in measurements)
                _allRows.Add(new LimitSampleRow(m));

            var modules = _allRows.Select(r => r.Module).Distinct().OrderBy(m => m).ToList();
            Modules.Add("(all)");
            foreach (var m in modules)
                Modules.Add(m);
            SelectedModule = modules.Count == 1 ? modules[0] : "(all)";

            StatusMessage = $"Loaded {_allRows.Count} measurement(s) across {modules.Count} module(s). " +
                            "Mark each as Should Pass / Should Fail / Ignore, then Save.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Query failed: " + ex.Message;
        }
    }

    /// <summary>Pre-fill expectations from an existing MSA_&lt;module&gt;.json for the selected module.</summary>
    [RelayCommand]
    private void LoadExisting()
    {
        var module = ResolveTargetModule();
        if (module is null)
            return;

        try
        {
            var existing = MsaReferenceFile.Load(_config.MsaReferencePath, module);
            if (existing is null)
            {
                StatusMessage = $"No existing MSA_{module}.json found.";
                return;
            }

            MsaVersion = string.IsNullOrWhiteSpace(existing.MsaVersion) ? MsaVersion : existing.MsaVersion;

            // Apply expectations to the loaded rows; add rows for entries not currently loaded.
            // A display_name can repeat (same measurement on several cameras) — apply to all matches.
            foreach (var (name, expected) in existing.LimitSampleExpected)
            {
                var exp = expected ? Expectation.ShouldFail : Expectation.ShouldPass;
                var matches = _allRows.Where(r => r.Module == module &&
                    string.Equals(r.DisplayName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count > 0)
                    foreach (var row in matches)
                        row.Expectation = exp;
                else
                    _allRows.Add(new LimitSampleRow(name, module, exp));
            }

            ApplyModuleFilter();
            StatusMessage = $"Loaded {existing.LimitSampleExpected.Count} expectation(s) from MSA_{module}.json.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Load failed: " + ex.Message;
        }
    }

    /// <summary>Reset every visible row to Ignore (remove from the reference).</summary>
    [RelayCommand]
    private void ClearMarks()
    {
        foreach (var r in Rows)
            r.Expectation = Expectation.Ignore;
        StatusMessage = "Cleared all marks on the visible rows.";
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_config.MsaReferencePath))
        {
            StatusMessage = "Cannot save: [MSA] ReferencePath is not set in Harry.ini.";
            MessageBox.Show("Set [MSA] ReferencePath in Harry.ini before saving.", "No reference folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var module = ResolveTargetModule();
        if (module is null)
        {
            StatusMessage = "Select a single module (not '(all)') before saving.";
            return;
        }

        // Teach guard (task 3): a controller that produced no real OK/NOK judgement (only status 2/99,
        // i.e. every row is Ignore) must NOT be a teach source. If NO camera of the module judged, refuse.
        var moduleRows = _allRows.Where(r => r.Module == module).ToList();
        var camerasJudged = moduleRows
            .GroupBy(r => r.CameraName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Any(r => r.Expectation != Expectation.Ignore), StringComparer.OrdinalIgnoreCase);
        var notJudged = camerasJudged.Where(kv => !kv.Value).Select(kv => kv.Key).OrderBy(c => c).ToList();

        if (camerasJudged.Count > 0 && camerasJudged.Values.All(v => !v))
        {
            StatusMessage = $"Kamera hat nicht bewertet – Einlernen nicht möglich (Modul {module}: nur Status 2/99).";
            MessageBox.Show(
                $"Der/die Controller von {module} haben für dieses Teil keine Bewertung (Status 0/1) geliefert – nur Status 2/99.\n\n" +
                "Einlernen nicht möglich. Bitte Kameraprogramm/-modus prüfen und ein Grenzmuster verwenden, das die Kamera als NOK erkennt.",
                "Einlernen nicht möglich", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var marked = moduleRows.Where(r => r.Expectation != Expectation.Ignore).ToList();
        if (marked.Count == 0)
        {
            StatusMessage = $"No measurements marked for module {module}.";
            return;
        }

        try
        {
            // Preserve the existing references (xm) block if a file already exists.
            var file = MsaReferenceFile.Load(_config.MsaReferencePath, module) ?? new MsaReferenceFile();
            file.Module = module;
            file.MsaVersion = MsaVersion.Trim();

            // The reference is keyed by display_name, which can repeat across cameras —
            // collapse duplicates into one entry (ShouldFail wins) and flag any conflicts.
            var expected = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var conflicts = new List<string>();
            foreach (var group in marked.GroupBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var anyFail = group.Any(r => r.Expectation == Expectation.ShouldFail);
                var anyPass = group.Any(r => r.Expectation == Expectation.ShouldPass);
                if (anyFail && anyPass)
                    conflicts.Add(group.Key);
                expected[group.Key] = anyFail; // prepared error (reject) wins over accept
            }
            file.LimitSampleExpected = expected;

            var path = file.Save(_config.MsaReferencePath);
            var fails = expected.Count(kv => kv.Value);
            var conflictNote = conflicts.Count == 0
                ? string.Empty
                : $"  ⚠ {conflicts.Count} duplicate name(s) had mixed marks → set to ShouldFail: {string.Join(", ", conflicts.Take(5))}";
            // Task 3: a reference with no prepared errors (ShouldFail) can never prove a rejection — warn.
            var vacuousNote = fails == 0
                ? "  ⚠ Keine erwarteten Fehler (ShouldFail) — die Referenz prüft nichts; die MSA-Auswertung meldet dann INVALID."
                : string.Empty;
            var notJudgedNote = notJudged.Count == 0
                ? string.Empty
                : $"  ⚠ Nicht bewertet (nur Status 2/99): {string.Join(", ", notJudged.Take(5))}";
            StatusMessage = $"Saved {Path.GetFileName(path)} — {expected.Count} entries ({fails} should-fail).{conflictNote}{vacuousNote}{notJudgedNote}";
        }
        catch (Exception ex)
        {
            StatusMessage = "Save failed: " + ex.Message;
            MessageBox.Show(ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>The concrete module to act on: the selected one, or the only loaded module.</summary>
    private string? ResolveTargetModule()
    {
        if (!string.IsNullOrEmpty(SelectedModule) && SelectedModule != "(all)")
            return SelectedModule;

        var distinct = _allRows.Select(r => r.Module).Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }
}
