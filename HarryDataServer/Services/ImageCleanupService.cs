using System.IO;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// Implements the retention job (CLAUDE.md section 14, "RetentionJob"): runs at startup and
/// then daily. The NAS moves full-res NG/Diagnostic/GoldenSample images and finished collages
/// out of their <c>…\Input</c> folder into <c>…\YYYY\MM\DD</c> day-folders. This service walks
/// those day-folders and deletes whole folders whose date (taken from the FOLDER NAME, not the
/// file timestamps) is older than the configured retention. It also drops expired DB partitions
/// and, for NG, removes the linked low-res individual images. Never blocks other subsystems.
/// </summary>
public sealed class ImageCleanupService : IImageCleanupService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    private readonly IConfigService _config;
    private readonly PartitionManager _partitions;
    private readonly IDatabaseService _database;
    private readonly ILogService _log;

    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _started;

    public ImageCleanupService(
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

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _task = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _log.Information("Image cleanup service started (daily retention).");
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

        // NG day-folders also drag their linked low-res individual images (SOW §5.2.3): NG parts
        // produce no collage, so the low-res images are kept until the matching full-res NG image
        // is deleted here — never earlier.
        CleanupSortedDayFolders(nas.HighResNgPath, nas.RetentionNgDays, nas.LowResIndividualPath);
        CleanupSortedDayFolders(nas.HighResDiagnosticPath, nas.RetentionDiagnosticDays, null);
        CleanupSortedDayFolders(nas.HighResGoldenSamplePath, nas.RetentionGoldenSampleDays, null);
        CleanupSortedDayFolders(nas.CollagePath, nas.RetentionCollageDays, null);

        // Drop expired DB partitions (never DELETE) once the database is ready.
        if (_database.Status == DatabaseStatus.Ready)
        {
            var days = _config.Config.MySql.RetentionPeriodDays;
            foreach (var table in DatabaseSchema.PartitionedTables)
                await _partitions.DropOldPartitionsAsync(table, days, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Delete whole NAS-sorted day-folders (YYYY\MM\DD) older than the retention period. The age
    /// comes from the FOLDER NAME — every image in a day-folder shares that date — not from file
    /// timestamps (which a NAS move may not preserve). For NG, the matching low-res individual
    /// images (linked by the 12-char serial prefix) are deleted alongside the full-res folder.
    /// </summary>
    private void CleanupSortedDayFolders(string basePath, int retentionDays, string? linkedLowResBase)
    {
        if (retentionDays <= 0)
            return;

        var root = ImageFileName.SortedRoot(basePath);
        if (root is null || !Directory.Exists(root))
            return;

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
                _log.Debug("Could not delete day-folder {Dir}: {Message}", dayPath, ex.Message);
            }
        }

        if (deletedFolders > 0)
            _log.Information("Retention: deleted {Folders} day-folder(s) older than {Days} days in {Root}{Linked}.",
                deletedFolders, retentionDays, root,
                deletedLowRes > 0 ? $" (+{deletedLowRes} linked low-res)" : string.Empty);
    }

    /// <summary>
    /// Yield each <c>YYYY\MM\DD</c> day-folder under a sorted root together with its date
    /// (parsed from the folder names). Non-date folders such as <c>Input</c> are skipped.
    /// </summary>
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

    /// <summary>
    /// Delete every low-res image under the low-res sorted root whose filename starts with one
    /// of the given 12-char serial prefixes (the NG full-res images being removed, SOW §5.2.3).
    /// </summary>
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

    /// <summary>
    /// The image search key (CLAUDE.md §6/§11): the first 12 characters of the serial, which
    /// every related image filename starts with. Null if the name is too short.
    /// </summary>
    private static string? SerialPrefix(string fileName) =>
        fileName.Length >= 12 ? fileName[..12] : null;

    private IEnumerable<string> EnumerateFilesSafe(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            _log.Debug("Could not enumerate {Dir}: {Message}", directory, ex.Message);
            return Array.Empty<string>();
        }
    }

    private IEnumerable<string> EnumerateDirsSafe(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex)
        {
            _log.Debug("Could not enumerate dirs in {Dir}: {Message}", directory, ex.Message);
            return Array.Empty<string>();
        }
    }
}
