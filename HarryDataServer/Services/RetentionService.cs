using System.IO;
using HarryDataServer.Infrastructure;
using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>
/// The one central retention job (CLAUDE.md §11a). Runs ~1 min after startup and then every 24 h.
/// Every target has its own age in days from the <c>[Retention]</c> section; <b>0 = never delete</b>.
///
/// <para>Targets:</para>
/// <list type="bullet">
///   <item>Images — NG (+linked low-res), Diagnostic, GoldenSample, Collage, Backup: whole NAS-sorted
///     <c>YYYY\MM\DD</c> day-folders older than retention (date from the FOLDER name).</item>
///   <item>Input leftovers — files left in a <c>…\Input</c> folder past a few days (failed pipeline
///     runs); deleted with a WARNING. The low-res Input keeps NG images (they are governed by the NG
///     linkage), so NG-flagged files there are skipped.</item>
///   <item>MSA reports — dated folders under <c>[MSA] ReportPath</c> (default 0 = never; QS evidence).</item>
///   <item>CSV — Evaluation / Merge / ExtraResults <c>YYYY\MM\DD</c> day-folders.</item>
///   <item>Database — DROP PARTITION on the partitioned production tables + batch DELETE on dmcserial
///     (production age) and the MSA tables (default 0 = never). Master data is never touched.</item>
/// </list>
/// Nothing is silent: each target logs an INFO line (n deleted / nothing to do / disabled), and every
/// path or access failure is a WARNING carrying the path. Never blocks other subsystems.
/// </summary>
public sealed class RetentionService : IRetentionService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    // DB batch delete: bound each statement so it never holds a long lock; pause between batches.
    private const int DbBatchSize = 10_000;
    private static readonly TimeSpan DbBatchPause = TimeSpan.FromMilliseconds(200);

    private readonly IConfigService _config;
    private readonly PartitionManager _partitions;
    private readonly IDatabaseService _database;
    private readonly ILogService _log;

    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _started;

    public RetentionService(
        IConfigService config,
        PartitionManager partitions,
        IDatabaseService database,
        ILogService log)
    {
        _config = config;
        _partitions = partitions;
        _database = database;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;

        // Deprecation notices for any legacy retention keys still used as a fallback (logged once).
        foreach (var note in _config.Config.Retention.Deprecations)
            _log.Warning("Config: {Note}.", note);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _task = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _log.Information("Retention service started (central [Retention]; runs at startup + every 24h).");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_task is not null)
        {
            try { await _task.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try { await Task.Delay(StartupDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await RunRetentionAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.Error(ex, "Retention job failed."); }

            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunRetentionAsync(CancellationToken ct)
    {
        var nas = _config.Config.Nas;
        var csv = _config.Config.Csv;
        var msa = _config.Config.Msa;
        var ret = _config.Config.Retention;

        // --- Images: whole day-folders by age (NG also drags its linked low-res) ---
        CleanupSortedDayFolders("Images/NG", nas.HighResNgPath, ret.ImagesNg, nas.LowResIndividualPath);
        CleanupSortedDayFolders("Images/Diagnostic", nas.HighResDiagnosticPath, ret.ImagesDiagnostic, null);
        CleanupSortedDayFolders("Images/GoldenSample", nas.HighResGoldenSamplePath, ret.ImagesGoldenSample, null);
        CleanupSortedDayFolders("Images/Collage", nas.CollagePath, ret.ImagesCollage, null);
        CleanupSortedDayFolders("Images/Backup", nas.BackupFolder, ret.ImagesBackup, null);

        // --- Input leftovers: files stuck in an \Input folder (failed runs) ---
        CleanupInputLeftovers("Input/LowRes", nas.LowResIndividualPath, ret.ImagesInputLeftovers, skipNgFlagged: true);
        CleanupInputLeftovers("Input/Collage", nas.CollagePath, ret.ImagesInputLeftovers, skipNgFlagged: false);
        CleanupInputLeftovers("Input/NG", nas.HighResNgPath, ret.ImagesInputLeftovers, skipNgFlagged: false);
        CleanupInputLeftovers("Input/Diagnostic", nas.HighResDiagnosticPath, ret.ImagesInputLeftovers, skipNgFlagged: false);
        CleanupInputLeftovers("Input/GoldenSample", nas.HighResGoldenSamplePath, ret.ImagesInputLeftovers, skipNgFlagged: false);

        // --- MSA reports: dated top-level folders under ReportPath (default 0 = never) ---
        CleanupDatedTopFolders("Reports/MSA", msa.ReportPath, ret.ReportsMsa);

        // --- CSV exports: YYYY\MM\DD day-folders ---
        CleanupSortedDayFolders("CSV/Merge", csv.BasePath, ret.CsvMerge, null);
        CleanupSortedDayFolders("CSV/Evaluation", csv.MsaPath, ret.CsvEvaluation, null);
        CleanupSortedDayFolders("CSV/ExtraResults", csv.DiagnosticPath, ret.CsvExtraResults, null);

        // --- Database ---
        if (_database.Status == DatabaseStatus.Ready)
        {
            // Partitioned production tables: DROP PARTITION (never DELETE — standing rule).
            if (ret.DatabaseProduction > 0)
            {
                foreach (var table in DatabaseSchema.PartitionedTables)
                {
                    try { await _partitions.DropOldPartitionsAsync(table, ret.DatabaseProduction, ct).ConfigureAwait(false); }
                    catch (Exception ex) { _log.Warning("Retention: DB/{Table} partition drop failed: {Message}", table, ex.Message); }
                }
            }

            // Non-partitioned tables: bounded batch DELETE by age.
            await BatchDeleteByAgeAsync("DB/dmcserial", "dmcserial", "created_at", ret.DatabaseProduction, ct).ConfigureAwait(false);
            await BatchDeleteByAgeAsync("DB/msa_measurements", "msa_measurements", "measured_at", ret.DatabaseMsa, ct).ConfigureAwait(false);
            await BatchDeleteByAgeAsync("DB/msa_results", "msa_results", "evaluated_at", ret.DatabaseMsa, ct).ConfigureAwait(false);
        }
        else
        {
            _log.Debug("Retention: database not ready — DB targets skipped this cycle.");
        }
    }

    // ---- Images / CSV: whole NAS-sorted YYYY\MM\DD day-folders --------------------------------

    private void CleanupSortedDayFolders(string target, string basePath, int retentionDays, string? linkedLowResBase)
    {
        if (retentionDays <= 0)
        {
            _log.Information("Retention: {Target} – disabled (0 = never).", target);
            return;
        }
        if (string.IsNullOrWhiteSpace(basePath))
            return;

        var root = ImageFileName.SortedRoot(basePath);
        if (root is null || !Directory.Exists(root))
        {
            _log.Information("Retention: {Target} – nothing to do (no folder {Root}).", target, root ?? basePath);
            return;
        }

        var cutoff = DateTime.Today.AddDays(-retentionDays);
        var lowResRoot = string.IsNullOrWhiteSpace(linkedLowResBase) ? null : ImageFileName.SortedRoot(linkedLowResBase);

        var deletedFolders = 0;
        var deletedLowRes = 0;

        foreach (var (dayPath, date) in EnumerateDayFolders(root))
        {
            if (date >= cutoff)
                continue;

            try
            {
                if (lowResRoot is not null && Directory.Exists(lowResRoot))
                {
                    var prefixes = EnumerateFilesSafe(dayPath)
                        .Select(f => SerialPrefix(Path.GetFileName(f)))
                        .Where(p => p is not null)
                        .Select(p => p!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    deletedLowRes += DeleteLinkedLowRes(lowResRoot, prefixes);
                }

                Directory.Delete(dayPath, recursive: true);
                deletedFolders++;
            }
            catch (Exception ex)
            {
                _log.Warning("Retention: {Target} – could not delete day-folder {Dir}: {Message}", target, dayPath, ex.Message);
            }
        }

        if (deletedFolders > 0)
            _log.Information("Retention: {Target} – {Folders} day-folder(s) deleted (older than {Cutoff:yyyy-MM-dd}){Linked}.",
                target, deletedFolders, cutoff, deletedLowRes > 0 ? $" (+{deletedLowRes} linked low-res)" : string.Empty);
        else
            _log.Information("Retention: {Target} – 0, nothing to do (older than {Cutoff:yyyy-MM-dd}).", target, cutoff);
    }

    // ---- MSA reports: dated top-level folders (yyyy-MM-dd) ------------------------------------

    private void CleanupDatedTopFolders(string target, string root, int retentionDays)
    {
        if (retentionDays <= 0)
        {
            _log.Information("Retention: {Target} – disabled (0 = never).", target);
            return;
        }
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _log.Information("Retention: {Target} – nothing to do (no folder {Root}).", target, root);
            return;
        }

        var cutoff = DateTime.Today.AddDays(-retentionDays);
        var deleted = 0;

        foreach (var dir in EnumerateDirsSafe(root))
        {
            var name = Path.GetFileName(dir);
            if (!DateTime.TryParseExact(name, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date))
                continue;
            if (date >= cutoff)
                continue;

            try
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
            catch (Exception ex)
            {
                _log.Warning("Retention: {Target} – could not delete report folder {Dir}: {Message}", target, dir, ex.Message);
            }
        }

        _log.Information(deleted > 0
                ? "Retention: {Target} – {Deleted} report folder(s) deleted (older than {Cutoff:yyyy-MM-dd})."
                : "Retention: {Target} – 0, nothing to do (older than {Cutoff:yyyy-MM-dd}).",
            target, deleted, cutoff);
    }

    // ---- Input leftovers: files stuck in an \Input folder ------------------------------------

    private void CleanupInputLeftovers(string target, string inputPath, int retentionDays, bool skipNgFlagged)
    {
        if (retentionDays <= 0)
            return; // 0 = never (kept quiet; the day-folder targets already report the disabled state)
        if (string.IsNullOrWhiteSpace(inputPath) || !Directory.Exists(inputPath))
            return;

        var cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
        var deleted = 0;

        foreach (var file in EnumerateTopFilesSafe(inputPath))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoffUtc)
                    continue;

                // In the low-res Input, NG images are kept intentionally (removed with their NG
                // full-res via the linkage), so never age those out here.
                if (skipNgFlagged)
                {
                    var parsed = ImageFileName.TryParse(Path.GetFileName(file));
                    if (parsed is not null && parsed.Overall == "0")
                        continue;
                }

                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                _log.Warning("Retention: {Target} – could not delete leftover {File}: {Message}", target, file, ex.Message);
            }
        }

        if (deleted > 0)
            _log.Warning("Retention: {Target} – {Deleted} leftover file(s) deleted from '{Path}' (older than {Days} days; failed pipeline runs).",
                target, deleted, inputPath, retentionDays);
    }

    // ---- Database: bounded batch DELETE by age -----------------------------------------------

    private async Task BatchDeleteByAgeAsync(string target, string table, string dateColumn, int retentionDays, CancellationToken ct)
    {
        if (retentionDays <= 0)
        {
            _log.Information("Retention: {Target} – disabled (0 = never).", target);
            return;
        }

        var cutoff = DateTime.Now.Date.AddDays(-retentionDays);
        long total = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int affected;
                await using (var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false))
                await using (var cmd = new MySqlCommand(
                    $"DELETE FROM `{table}` WHERE `{dateColumn}` < @cutoff LIMIT {DbBatchSize};", conn))
                {
                    cmd.Parameters.AddWithValue("@cutoff", cutoff);
                    affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                total += affected;
                if (affected < DbBatchSize)
                    break;

                // Short pause between batches so we never hold a long lock or saturate IO.
                await Task.Delay(DbBatchPause, ct).ConfigureAwait(false);
            }

            _log.Information(total > 0
                    ? "Retention: {Target} – {Rows} row(s) deleted (older than {Cutoff:yyyy-MM-dd})."
                    : "Retention: {Target} – 0, nothing to do (older than {Cutoff:yyyy-MM-dd}).",
                target, total, cutoff);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _log.Warning("Retention: {Target} – batch delete failed after {Rows} row(s): {Message}", target, total, ex.Message);
        }
    }

    // ---- shared helpers (ported from the former ImageCleanupService) --------------------------

    private IEnumerable<(string Path, DateTime Date)> EnumerateDayFolders(string root)
    {
        foreach (var yearDir in EnumerateDirsSafe(root))
        {
            if (!TryNum(Path.GetFileName(yearDir), 1000, 9999, out var year))
                continue;
            foreach (var monthDir in EnumerateDirsSafe(yearDir))
            {
                if (!TryNum(Path.GetFileName(monthDir), 1, 12, out var month))
                    continue;
                foreach (var dayDir in EnumerateDirsSafe(monthDir))
                {
                    if (!TryNum(Path.GetFileName(dayDir), 1, 31, out var day))
                        continue;

                    DateTime date;
                    try { date = new DateTime(year, month, day); }
                    catch (ArgumentOutOfRangeException) { continue; }
                    yield return (dayDir, date);
                }
            }
        }
    }

    private static bool TryNum(string text, int min, int max, out int value) =>
        int.TryParse(text, out value) && value >= min && value <= max;

    private int DeleteLinkedLowRes(string lowResRoot, IReadOnlySet<string> serialPrefixes)
    {
        if (serialPrefixes.Count == 0)
            return 0;

        var deleted = 0;
        foreach (var file in EnumerateFilesSafe(lowResRoot))
        {
            var name = Path.GetFileName(file);
            if (name.Length < 12 || !serialPrefixes.Contains(name[..12]))
                continue;
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                _log.Debug("Could not delete linked low-res image {File}: {Message}", file, ex.Message);
            }
        }
        return deleted;
    }

    private static string? SerialPrefix(string fileName) =>
        fileName.Length >= 12 ? fileName[..12] : null;

    private IEnumerable<string> EnumerateFilesSafe(string directory)
    {
        try { return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories); }
        catch (Exception ex) { _log.Debug("Could not enumerate {Dir}: {Message}", directory, ex.Message); return Array.Empty<string>(); }
    }

    private IEnumerable<string> EnumerateTopFilesSafe(string directory)
    {
        try { return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) { _log.Warning("Retention: could not enumerate '{Dir}': {Message}", directory, ex.Message); return Array.Empty<string>(); }
    }

    private IEnumerable<string> EnumerateDirsSafe(string directory)
    {
        try { return Directory.EnumerateDirectories(directory); }
        catch (Exception ex) { _log.Debug("Could not enumerate dirs in {Dir}: {Message}", directory, ex.Message); return Array.Empty<string>(); }
    }
}
