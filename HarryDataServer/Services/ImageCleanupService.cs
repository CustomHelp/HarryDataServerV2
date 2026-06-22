using System.IO;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// Implements the retention job (CLAUDE.md section 14, "RetentionJob"): runs at
/// startup and then daily. Deletes aged NG/Diagnostic/GoldenSample images, drops
/// expired DB partitions, and (on Part Exit = NG) removes the now-orphaned OK
/// images of that part. Never blocks other subsystems; all failures are logged.
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

        // NG full-res images also drag their linked low-res individual images with them
        // (SOW §5.2.3): NG parts produce no collage, so the low-res images are kept until
        // the matching full-res NG image is deleted here — never earlier.
        DeleteAgedNgImages(nas.HighResNgPath, nas.LowResIndividualPath, nas.RetentionNgDays);
        DeleteAgedFiles(nas.HighResDiagnosticPath, nas.RetentionDiagnosticDays);
        DeleteAgedFiles(nas.HighResGoldenSamplePath, nas.RetentionGoldenSampleDays);

        // Drop expired DB partitions (never DELETE) once the database is ready.
        if (_database.Status == DatabaseStatus.Ready)
        {
            var days = _config.Config.MySql.RetentionPeriodDays;
            foreach (var table in DatabaseSchema.PartitionedTables)
                await _partitions.DropOldPartitionsAsync(table, days, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Delete aged full-resolution NG images and, for each one removed, the matching
    /// low-resolution individual images (linked by the 12-char serial prefix, SOW §5.2.3).
    /// The low-res images of an NG part are deliberately retained until this point — they
    /// are not deleted at part exit because no collage consumes them.
    /// </summary>
    private void DeleteAgedNgImages(string ngDirectory, string lowResDirectory, int retentionDays)
    {
        if (retentionDays <= 0 || string.IsNullOrWhiteSpace(ngDirectory) || !Directory.Exists(ngDirectory))
            return;

        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var deletedNg = 0;
        var deletedLowRes = 0;

        foreach (var file in EnumerateFilesSafe(ngDirectory))
        {
            try
            {
                if (File.GetLastWriteTime(file) >= cutoff)
                    continue;

                var prefix = SerialPrefix(Path.GetFileName(file));
                File.Delete(file);
                deletedNg++;
                deletedLowRes += DeleteLowResByPrefix(lowResDirectory, prefix);
            }
            catch (Exception ex)
            {
                _log.Debug("Could not delete NG image {File}: {Message}", file, ex.Message);
            }
        }

        if (deletedNg > 0)
            _log.Information("Retention: deleted {Ng} NG image(s) + {Low} linked low-res image(s) older than {Days} days in {Dir}.",
                deletedNg, deletedLowRes, retentionDays, ngDirectory);
    }

    /// <summary>Delete every low-res image whose filename starts with the given 12-char serial prefix.</summary>
    private int DeleteLowResByPrefix(string lowResDirectory, string? serialPrefix)
    {
        if (string.IsNullOrEmpty(serialPrefix) || string.IsNullOrWhiteSpace(lowResDirectory) || !Directory.Exists(lowResDirectory))
            return 0;

        var deleted = 0;
        foreach (var file in EnumerateFilesSafe(lowResDirectory))
        {
            if (!Path.GetFileName(file).StartsWith(serialPrefix, StringComparison.OrdinalIgnoreCase))
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
    /// The image search key (CLAUDE.md §6/§11): the first 12 characters of the serial,
    /// which every related image filename starts with. Null if the name is too short.
    /// </summary>
    private static string? SerialPrefix(string fileName) =>
        fileName.Length >= 12 ? fileName[..12] : null;

    /// <summary>Delete files older than <paramref name="retentionDays"/> under a directory tree.</summary>
    private void DeleteAgedFiles(string directory, int retentionDays)
    {
        if (retentionDays <= 0 || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var deleted = 0;

        foreach (var file in EnumerateFilesSafe(directory))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                _log.Debug("Could not delete {File}: {Message}", file, ex.Message);
            }
        }

        if (deleted > 0)
            _log.Information("Retention: deleted {Count} file(s) older than {Days} days in {Dir}.",
                deleted, retentionDays, directory);
    }

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
}
