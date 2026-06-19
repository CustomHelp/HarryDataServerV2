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
    private readonly ISpsServer _sps;
    private readonly PartitionManager _partitions;
    private readonly IDatabaseService _database;
    private readonly ILogService _log;

    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _started;

    public ImageCleanupService(
        IConfigService config,
        ISpsServer sps,
        PartitionManager partitions,
        IDatabaseService database,
        ILogService log)
    {
        _config = config;
        _sps = sps;
        _partitions = partitions;
        _database = database;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;

        _sps.PartExitReceived += OnPartExitReceived;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _task = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _log.Information("Image cleanup service started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _sps.PartExitReceived -= OnPartExitReceived;
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

        DeleteAgedFiles(nas.HighResNgPath, nas.RetentionNgDays);
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

    /// <summary>
    /// When a part leaves as NG, its intermediate OK images (M10/M20 etc.) are
    /// orphaned — delete them by the first 12 chars of the serial (CLAUDE.md section 11).
    /// </summary>
    private void OnPartExitReceived(object? sender, SpsPartExitEventArgs e)
    {
        if (e.Data.Result != PartResult.Ng)
            return;

        var serial = e.Data.Szid;
        if (string.IsNullOrWhiteSpace(serial) || serial.Length < 12)
            return;

        var prefix = serial[..12];
        var dir = _config.Config.Nas.LowResIndividualPath;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return;

        var deleted = 0;
        foreach (var file in EnumerateFilesSafe(dir))
        {
            if (!Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            try { File.Delete(file); deleted++; }
            catch (Exception ex) { _log.Debug("Could not delete orphaned {File}: {Message}", file, ex.Message); }
        }

        if (deleted > 0)
            _log.Information("Deleted {Count} orphaned OK image(s) for NG part {Serial}.", deleted, serial);
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
