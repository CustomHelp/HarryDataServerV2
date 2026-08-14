using System.IO;
using MySqlConnector;

namespace HarryDataServer.Services;

/// <summary>
/// Periodic free-disk watchdog (see <see cref="IDiskSpaceMonitor"/> for the incident that motivated
/// it). Every <c>[Monitoring] DiskCheckIntervalMinutes</c> it collects the drives the server really
/// writes to, reads their free space and reports each one that falls below
/// <c>DiskWarnFreeGB</c> (WARNING) or <c>DiskCriticalFreeGB</c> (ERROR).
///
/// <para><b>Which drives.</b> The watched paths come from the live configuration, so the list follows
/// the INI instead of being hardcoded. The two most important ones are not in the INI at all: MySQL's
/// <c>tmpdir</c> and <c>datadir</c> are asked of the running server (<c>SELECT @@tmpdir, @@datadir</c>)
/// — the tmpdir is a Windows service default on C: and is exactly what nobody thinks of. Several paths
/// usually share one drive; they are grouped per drive root and the log line names all users of it,
/// so the message says <i>what</i> is at risk, not just that a letter is full.</para>
///
/// <para><b>Log discipline (same rule as a camera outage).</b> A level change is logged once — going
/// bad and recovering both produce exactly one line — and a drive that stays bad is repeated only
/// every 6 h. So a permanently tight drive cannot inflate the warning counter, and a real change is
/// never buried. UNC paths are skipped (DriveInfo only understands <c>X:\</c>); a mapped network drive
/// (X:, Y:, Z:) is watched like any local one.</para>
///
/// <para>Read-only by design: it never deletes anything. Freeing space stays a human decision — this
/// service only makes sure the decision is asked for in time.</para>
/// </summary>
public sealed class DiskSpaceMonitor : IDiskSpaceMonitor
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    /// <summary>How long a drive may stay in a bad state before the message is repeated.</summary>
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromHours(6);

    private const double BytesPerGb = 1024d * 1024d * 1024d;

    private enum Level
    {
        Ok,
        Low,
        Critical,
    }

    private readonly IConfigService _config;
    private readonly IDatabaseService _database;
    private readonly ILogService _log;

    /// <summary>Last reported level per drive root + when it was logged (hysteresis, see class doc).</summary>
    private readonly Dictionary<string, (Level Level, DateTime LoggedAt)> _state =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MySQL's own paths, asked of the server once it is reachable.</summary>
    private readonly List<(string Label, string Path)> _mysqlPaths = new();

    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _started;

    public DiskSpaceMonitor(IConfigService config, IDatabaseService database, ILogService log)
    {
        _config = config;
        _database = database;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;

        var monitoring = _config.Config.Monitoring;
        if (monitoring.DiskWarnFreeGb <= 0)
        {
            _log.Information("Disk monitor disabled ([Monitoring] DiskWarnFreeGB = 0).");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _task = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _log.Information(
            "Disk monitor started (every {Interval} min; WARNING below {Warn} GB, ERROR below {Critical} GB free).",
            Math.Max(1, monitoring.DiskCheckIntervalMinutes), monitoring.DiskWarnFreeGb, monitoring.DiskCriticalFreeGb);
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

        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.Config.Monitoring.DiskCheckIntervalMinutes));

        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.Error(ex, "Disk monitor check failed."); }

            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        await EnsureMySqlPathsAsync(ct).ConfigureAwait(false);

        var monitoring = _config.Config.Monitoring;
        var warnGb = monitoring.DiskWarnFreeGb;
        // A critical threshold above the warning one would swallow the warning level entirely.
        var criticalGb = Math.Min(monitoring.DiskCriticalFreeGb, warnGb);

        // Group every configured path by its drive root so one drive produces one line naming all users.
        var usersPerDrive = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, path) in WatchedPaths())
        {
            var root = TryGetDriveRoot(path);
            if (root is null)
                continue;
            if (!usersPerDrive.TryGetValue(root, out var users))
                usersPerDrive[root] = users = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            users.Add(label);
        }

        foreach (var (root, users) in usersPerDrive)
        {
            DriveInfo drive;
            try
            {
                drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    _log.Debug("Disk monitor: drive {Drive} not ready — skipped.", root);
                    continue;
                }
            }
            catch (Exception ex)
            {
                _log.Debug("Disk monitor: drive {Drive} could not be read ({Message}) — skipped.", root, ex.Message);
                continue;
            }

            var freeGb = Math.Round(drive.AvailableFreeSpace / BytesPerGb, 2);
            var totalGb = Math.Round(drive.TotalSize / BytesPerGb, 1);
            var level = freeGb < criticalGb ? Level.Critical : freeGb < warnGb ? Level.Low : Level.Ok;

            Report(root, users, level, freeGb, totalGb, warnGb, criticalGb);
        }
    }

    /// <summary>Log a drive's state, but only on a level change or every <see cref="RepeatInterval"/>.</summary>
    private void Report(
        string root, SortedSet<string> users, Level level, double freeGb, double totalGb, int warnGb, int criticalGb)
    {
        var known = _state.TryGetValue(root, out var previous);
        var changed = !known || previous.Level != level;
        var due = level != Level.Ok && known && DateTime.Now - previous.LoggedAt >= RepeatInterval;

        // Nothing new to say: unchanged, and either fine or not yet due for a repeat.
        if (!changed && !due)
            return;

        // A drive that was fine and still is must not produce a line on the very first check.
        if (level == Level.Ok && !known)
        {
            _state[root] = (Level.Ok, DateTime.Now);
            _log.Debug("Disk monitor: {Drive} {Free} GB free of {Total} GB — used by: {Users}.",
                root, freeGb, totalGb, string.Join(", ", users));
            return;
        }

        _state[root] = (level, DateTime.Now);
        var used = string.Join(", ", users);

        switch (level)
        {
            case Level.Critical:
                _log.Error(
                    "Disk {Drive} is CRITICALLY full: {Free} GB free of {Total} GB (below {Threshold} GB). " +
                    "Used by: {Users}. Writes are about to fail — with MySQL's tmpdir on this drive, large " +
                    "queries (e.g. the MSA raw-data export) fail while small ones still work. Free space now.",
                    root, freeGb, totalGb, criticalGb, used);
                break;

            case Level.Low:
                _log.Warning(
                    "Disk {Drive} is running low: {Free} GB free of {Total} GB (below {Threshold} GB). Used by: {Users}.",
                    root, freeGb, totalGb, warnGb, used);
                break;

            default:
                _log.Information(
                    "Disk {Drive} recovered: {Free} GB free of {Total} GB. Used by: {Users}.",
                    root, freeGb, totalGb, used);
                break;
        }
    }

    /// <summary>
    /// Ask the running MySQL server where it puts temp files and data. Done once — the values cannot
    /// change without a server restart, and a restart restarts us too in practice. Until the database
    /// is reachable the rest of the check still runs.
    /// </summary>
    private async Task EnsureMySqlPathsAsync(CancellationToken ct)
    {
        if (_mysqlPaths.Count > 0 || _database.Status != DatabaseStatus.Ready)
            return;

        try
        {
            await using var conn = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = new MySqlCommand("SELECT @@tmpdir, @@datadir;", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return;

            // @@tmpdir may hold several paths separated by ';' on Windows — watch every one of them.
            var tmpDirs = reader.IsDBNull(0) ? Array.Empty<string>() : reader.GetString(0).Split(';');
            foreach (var dir in tmpDirs.Where(d => !string.IsNullOrWhiteSpace(d)))
                _mysqlPaths.Add(("MySQL tmpdir", dir.Trim()));

            if (!reader.IsDBNull(1))
                _mysqlPaths.Add(("MySQL datadir", reader.GetString(1).Trim()));

            if (_mysqlPaths.Count > 0)
                _log.Information("Disk monitor: watching MySQL paths — {Paths}.",
                    string.Join(" | ", _mysqlPaths.Select(p => $"{p.Label} {p.Path}")));
        }
        catch (Exception ex)
        {
            // Not fatal: the configured paths are still checked, and the next cycle retries.
            _log.Debug("Disk monitor: could not read MySQL tmpdir/datadir ({Message}); retrying next cycle.", ex.Message);
        }
    }

    /// <summary>Every path the server writes to, labelled for the log line.</summary>
    private IEnumerable<(string Label, string Path)> WatchedPaths()
    {
        foreach (var mysql in _mysqlPaths)
            yield return mysql;

        var c = _config.Config;
        yield return ("logs", c.General.LogFilePath);
        yield return ("CSV production", c.Csv.BasePath);
        yield return ("CSV diagnostic", c.Csv.DiagnosticPath);
        yield return ("diagnostic dump", c.Diagnostic.Path);
        yield return ("MSA reports", c.Msa.ReportPath);
        yield return ("MSA report fallback", c.Msa.ReportFallbackPath);
        yield return ("MSA references", c.Msa.ReferencePath);
        yield return ("images low-res", c.Nas.LowResIndividualPath);
        yield return ("images collage", c.Nas.CollagePath);
        yield return ("images NG", c.Nas.HighResNgPath);
        yield return ("images diagnostic", c.Nas.HighResDiagnosticPath);
        yield return ("images GoldenSample", c.Nas.HighResGoldenSamplePath);
        yield return ("images backup", c.Nas.BackupFolder);
        yield return ("collage output", c.Collage.ResultImagesPath);
    }

    /// <summary>
    /// The <c>X:\</c> root of a path, or null when there is nothing to measure. UNC paths
    /// (<c>\\server\share</c>) return null on purpose — DriveInfo cannot read them; a mapped drive
    /// letter pointing at the same share is watched normally.
    /// </summary>
    private static string? TryGetDriveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
                return null;
            return root;
        }
        catch
        {
            // An unparsable path is a configuration problem the owning service already reports.
            return null;
        }
    }
}
