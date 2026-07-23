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

    /// <summary>DMC of the currently loaded/scanned part — the key of the per-part reference file.</summary>
    private string _currentDmc = string.Empty;

    /// <summary>MSA/LimitSample BaseID the loaded part last ran under (task C) — stamped into the
    /// reference as source_base_id when saving. Empty when the part has no MSA run on record.</summary>
    private string _currentBaseId = string.Empty;

    /// <summary>Modules that can hold references — pre-filled so a module can be picked (and
    /// "Load existing" / the taught-parts list used) WITHOUT scanning a part first.</summary>
    private static readonly string[] KnownModules = { "M10", "M11", "M20", "M21", "M50" };

    public MainViewModel(QueryService query, HarryConfig config, ScannerCompanionClient scanner)
    {
        _query = query;
        _config = config;
        _scanner = scanner;
        ConfigFile = config.IniPath;
        ReferenceFolder = string.IsNullOrWhiteSpace(config.MsaReferencePath)
            ? "(not set in Harry.ini [MSA] ReferencePath)"
            : config.MsaReferencePath;

        // Pre-fill the module list so a module is selectable before any search (Load existing /
        // taught-parts list work immediately). The list stays fixed; a search only changes the selection.
        Modules.Add("(all)");
        foreach (var m in KnownModules)
            Modules.Add(m);
        SelectedModule = "(all)";

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
            if (string.IsNullOrWhiteSpace(_currentDmc))
                return "Saving to: (scan a part first — one reference file per DMC)";
            // Per-part reference file: <ReferencePath>\<Module>\LimitSamples\<DMC>.json (task A).
            return "Saving to: " + LimitSampleReference.PathFor(_config.MsaReferencePath, module, _currentDmc);
        }
    }

    public ObservableCollection<LimitSampleRow> Rows { get; } = new();
    public ObservableCollection<string> Modules { get; } = new();
    public Array Expectations => Enum.GetValues(typeof(Expectation));

    /// <summary>All taught parts (per-part reference files) of the selected module — DMC, when taught,
    /// and how many prepared errors — with open/delete per row (task A5).</summary>
    public ObservableCollection<TaughtPartRow> TaughtParts { get; } = new();

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
        RefreshTaughtParts();
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
        // Modules stays the fixed known list (filled at startup); a search only changes the selection.

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

            _currentDmc = !string.IsNullOrWhiteSpace(part.Dmc) ? part.Dmc!
                        : !string.IsNullOrWhiteSpace(part.SerialNumber) ? part.SerialNumber
                        : scan;
            // Task C: remember which MSA/LimitSample run this part came from (for source_base_id).
            _currentBaseId = await _query.GetLatestMsaBaseIdAsync(_currentDmc);

            var measurements = await _query.GetPartMeasurementsAsync(part);
            foreach (var m in measurements)
                _allRows.Add(new LimitSampleRow(m));

            var modules = _allRows.Select(r => r.Module).Distinct().OrderBy(m => m).ToList();
            SelectedModule = modules.Count == 1 ? modules[0] : "(all)";

            OnPropertyChanged(nameof(SavePathPreview));
            RefreshTaughtParts();
            StatusMessage = $"Loaded {_allRows.Count} measurement(s) for DMC {_currentDmc} across {modules.Count} module(s). " +
                            "Mark each as Should Pass / Should Fail / Ignore, then Save (one file per part).";
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

    /// <summary>Open the shared bilingual help window for this app (also on F1).</summary>
    [RelayCommand]
    private void ShowHelp() =>
        HelpWindow.Show(Application.Current?.MainWindow, SuiteHelp.LimitSample(AppVersion));

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

        if (string.IsNullOrWhiteSpace(_currentDmc))
        {
            StatusMessage = "Cannot save: the scanned part has no DMC/serial (a per-part reference needs a DMC).";
            MessageBox.Show("Das geladene Teil hat keinen DMC/keine Seriennummer — eine Per-Teil-Referenz braucht einen DMC.",
                "Kein DMC", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            // One file PER PART (task A): <ReferencePath>\<Module>\LimitSamples\<DMC>.json.
            var reference = new LimitSampleReference
            {
                Dmc = _currentDmc,
                Module = module,
                TaughtAt = DateTime.Now,
                SourceBaseId = _currentBaseId, // task C
                Controllers = marked.Select(r => r.CameraName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };

            // Keyed by display_name (can repeat across cameras) — collapse (ShouldFail wins), flag conflicts.
            var conflicts = new List<string>();
            foreach (var group in marked.GroupBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var anyFail = group.Any(r => r.Expectation == Expectation.ShouldFail);
                var anyPass = group.Any(r => r.Expectation == Expectation.ShouldPass);
                if (anyFail && anyPass)
                    conflicts.Add(group.Key);
                reference.Expected[group.Key] = anyFail ? LimitSampleReference.ShouldFail : LimitSampleReference.ShouldPass;
            }

            var path = reference.Save(_config.MsaReferencePath);
            var fails = reference.ExpectedRejectCount;
            var conflictNote = conflicts.Count == 0
                ? string.Empty
                : $"  ⚠ {conflicts.Count} duplicate name(s) had mixed marks → ShouldFail: {string.Join(", ", conflicts.Take(5))}";
            // Task A: a reference with no prepared errors (ShouldFail) is a valid GOOD reference (it must
            // pass everywhere), but a run made up of ONLY such parts is reported INVALID (nothing proven).
            var vacuousNote = fails == 0
                ? "  ⚠ Keine erwarteten Fehler (ShouldFail) — dies ist eine Gut-Referenz; ein Lauf, der NUR aus Gut-Referenzen besteht, meldet INVALID."
                : string.Empty;
            var notJudgedNote = notJudged.Count == 0
                ? string.Empty
                : $"  ⚠ Nicht bewertet (nur Status 2/99): {string.Join(", ", notJudged.Take(5))}";
            StatusMessage = $"Saved {path} — {reference.Expected.Count} entries ({fails} should-fail).{conflictNote}{vacuousNote}{notJudgedNote}";
            RefreshTaughtParts();
        }
        catch (Exception ex)
        {
            StatusMessage = "Save failed: " + ex.Message;
            MessageBox.Show(ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Taught parts list (per-part references of the selected module) ---

    /// <summary>Reload the taught-parts list for the current target module from the LimitSamples folder.</summary>
    private void RefreshTaughtParts()
    {
        TaughtParts.Clear();
        if (string.IsNullOrWhiteSpace(_config.MsaReferencePath))
            return;
        var module = ResolveTargetModule();
        if (module is null)
            return;
        // Baugleich modules (M10↔M11, M20↔M21) share references — show the module AND its mirror,
        // tagged with each part's OWN module so Open/Delete act on the correct file/folder.
        foreach (var r in LimitSampleReference.LoadAllWithMirror(_config.MsaReferencePath, module)
                     .OrderByDescending(r => r.TaughtAt))
            TaughtParts.Add(new TaughtPartRow(r.Dmc, r.TaughtAt, r.ExpectedRejectCount, r.Module));
    }

    /// <summary>
    /// Open a taught part for re-editing. The reference file only stores ShouldPass/ShouldFail, so to
    /// let ANY measurement be changed (incl. promoting an Ignore) the part's FULL measurement set is
    /// re-queried from the DB and the saved marks are overlaid on top. If the part is no longer in the
    /// DB, fall back to showing just the saved marks (old behaviour).
    /// </summary>
    [RelayCommand]
    private async Task OpenTaughtPartAsync(TaughtPartRow? part)
    {
        if (part is null)
            return;
        var reference = LimitSampleReference.Load(_config.MsaReferencePath, part.Module, part.Dmc);
        if (reference is null)
        {
            StatusMessage = $"Could not load taught part {part.Dmc}.";
            return;
        }

        _currentDmc = reference.Dmc;
        // Preserve the taught run's BaseID (task C); look it up if the old file had none.
        _currentBaseId = string.IsNullOrWhiteSpace(reference.SourceBaseId)
            ? await _query.GetLatestMsaBaseIdAsync(reference.Dmc)
            : reference.SourceBaseId;
        _allRows.Clear();

        // Re-query the whole part so every measurement is shown (incl. Ignore); overlay saved marks.
        var loadedFromDb = false;
        try
        {
            var dbPart = await _query.FindPartForInspectionAsync(reference.Dmc);
            if (dbPart is not null)
            {
                foreach (var m in await _query.GetPartMeasurementsAsync(dbPart))
                {
                    var row = new LimitSampleRow(m);
                    // Saved marks apply to THIS module only (a display_name can repeat across modules).
                    if (m.Module == part.Module &&
                        reference.Expected.TryGetValue(m.DisplayName, out var saved))
                        row.Expectation = string.Equals(saved, LimitSampleReference.ShouldFail, StringComparison.OrdinalIgnoreCase)
                            ? Expectation.ShouldFail : Expectation.ShouldPass;
                    _allRows.Add(row);
                }
                loadedFromDb = _allRows.Count > 0;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Re-query failed, showing saved marks only: " + ex.Message;
        }

        // Fallback: part no longer in the DB → show just the saved marks (no Ignore rows available).
        if (!loadedFromDb)
        {
            foreach (var (name, expectation) in reference.Expected)
            {
                var exp = string.Equals(expectation, LimitSampleReference.ShouldFail, StringComparison.OrdinalIgnoreCase)
                    ? Expectation.ShouldFail : Expectation.ShouldPass;
                _allRows.Add(new LimitSampleRow(name, part.Module, exp));
            }
        }

        // Keep the fixed module list; just select the part's module. If it is already selected the
        // setter is a no-op, so refresh the grid/list explicitly.
        if (SelectedModule == part.Module)
        {
            ApplyModuleFilter();
            RefreshTaughtParts();
        }
        else
        {
            SelectedModule = part.Module;   // triggers ApplyModuleFilter + RefreshTaughtParts
        }
        OnPropertyChanged(nameof(SavePathPreview));
        StatusMessage = loadedFromDb
            ? $"Loaded taught part {part.Dmc} — {Rows.Count} measurement(s) shown ({reference.ExpectedRejectCount} should-fail). Change any mark (incl. Ignore) and Save."
            : $"Loaded taught part {part.Dmc} (saved marks only — part not in DB): {reference.Expected.Count} entries.";
    }

    /// <summary>Delete one taught part's reference file after confirmation.</summary>
    [RelayCommand]
    private void DeleteTaughtPart(TaughtPartRow? part)
    {
        if (part is null)
            return;
        if (MessageBox.Show($"Delete the LimitSample reference for DMC {part.Dmc} (module {part.Module})?",
                "Delete taught part", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var removed = LimitSampleReference.Delete(_config.MsaReferencePath, part.Module, part.Dmc);
            StatusMessage = removed ? $"Deleted taught part {part.Dmc}." : $"No file found for {part.Dmc}.";
            RefreshTaughtParts();
        }
        catch (Exception ex)
        {
            StatusMessage = "Delete failed: " + ex.Message;
            MessageBox.Show(ex.Message, "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
