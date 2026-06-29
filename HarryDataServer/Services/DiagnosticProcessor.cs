using System.Collections.Concurrent;
using HarryDataServer.Communication;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// Background diagnostic pipeline. Subscribes to every camera's
/// <see cref="TcpCameraClient.DiagnosticReceived"/> event, enqueues a row (no I/O on
/// the receive thread) and dumps it straight to a rotating CSV file — never to the
/// database (CLAUDE.md sections 4 + 13). A diagnostic telegram has no fixed schema, so
/// each row is a RAW dump: <c>ReceivedAt</c>, Serial1 (≤22), Serial2 (32), then every
/// remaining token (the word "Diagnostic", the label and all values) plain left-to-right.
/// Rows may therefore have different column counts. Files are
/// <c>Diagnostic_&lt;DDMMYY_HHMMSS&gt;.csv</c>, rotated every <c>[Diagnostic] MaxRows</c> rows.
/// </summary>
public sealed class DiagnosticProcessor : IDiagnosticProcessor
{
    private const int MaxQueue = 200_000;
    private const int MaxItemsPerFlush = 10_000;

    private readonly ICameraService _cameras;
    private readonly ILogService _log;
    private readonly bool _enabled;
    private readonly string _path;
    private readonly int _maxRows;
    private readonly TimeSpan _flushInterval;

    private readonly ConcurrentQueue<string[]> _queue = new();
    private CsvFileWriter? _csv;
    private CancellationTokenSource? _cts;
    private Task? _flushTask;
    private long _totalWritten;
    private bool _started;

    public DiagnosticProcessor(ICameraService cameras, IConfigService config, ILogService log)
    {
        _cameras = cameras;
        _log = log;

        // Enable flag stays on the existing [CSV] CSVDiagnostic_Save; the output path + row cap
        // come from the [Diagnostic] section (path falls back to [CSV] CSV_DiagnosticPath).
        var diag = config.Config.Diagnostic;
        _enabled = config.Config.Csv.DiagnosticSave && !string.IsNullOrWhiteSpace(diag.Path);
        _path = diag.Path;
        _maxRows = diag.MaxRows;
        _flushInterval = TimeSpan.FromSeconds(Math.Max(1, config.Config.SqlSettings.SaveIntervalSeconds));
    }

    public int PendingCount => _queue.Count;
    public long TotalWritten => Interlocked.Read(ref _totalWritten);
    public event Action? StatsChanged;

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;

        if (!_enabled)
        {
            _log.Information("Diagnostic CSV disabled or no path configured; processor idle.");
            return Task.CompletedTask;
        }

        // Raw dump: label-first filename (Diagnostic_<stamp>.csv) directly in the configured
        // folder (no date subfolders, no header row — variable column count per row).
        _csv = new CsvFileWriter(_path, _maxRows, dateSubfolders: false, _log, labelFirst: true);
        _csv.Configure(Array.Empty<string>(), "Diagnostic");

        foreach (var client in _cameras.Clients)
            client.DiagnosticReceived += OnDiagnosticReceived;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _flushTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _log.Information("Diagnostic processor started; writing to {Path}.", _path);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_enabled)
            return;

        foreach (var client in _cameras.Clients)
            client.DiagnosticReceived -= OnDiagnosticReceived;

        _cts?.Cancel();
        if (_flushTask is not null)
        {
            try { await _flushTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        _csv?.Dispose();
    }

    // --- Receive side (camera thread; in-memory only) ---

    private void OnDiagnosticReceived(object? sender, DiagnosticTelegramEventArgs e)
    {
        if (_queue.Count >= MaxQueue)
        {
            _log.Warning("Diagnostic queue full ({Max}); dropping {Camera} telegram.",
                MaxQueue, e.Telegram.ControllerName);
            return;
        }

        var t = e.Telegram;

        // Raw row, plain left-to-right: ReceivedAt, Serial1 (≤22), Serial2 (32), then every
        // remaining token from the "Diagnostic" word onward (word + label + arbitrary values),
        // exactly as received and uninterpreted.
        var start = t.DiagnosticStart >= 0 ? t.DiagnosticStart : t.Fields.Length;
        var row = new string[3 + (t.Fields.Length - start)];
        row[0] = FileNaming.Stamp(DateTime.Now);   // ReceivedAt (DDMMYY_HHMMSS)
        row[1] = t.Serial1;
        row[2] = t.Serial2;
        for (var i = start; i < t.Fields.Length; i++)
            row[3 + (i - start)] = t.Fields[i];

        _queue.Enqueue(row);
    }

    // --- Flush side (dedicated background task; CSV only, no DB) ---

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_flushInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try { Flush(); }
            catch (Exception ex) { _log.Error(ex, "Diagnostic CSV flush failed."); }
        }

        try { Flush(); }
        catch (Exception ex) { _log.Error(ex, "Final diagnostic CSV flush failed."); }
    }

    private void Flush()
    {
        if (_csv is null || _queue.IsEmpty)
            return;

        var written = 0;
        while (written < MaxItemsPerFlush && _queue.TryDequeue(out var row))
        {
            _csv.WriteRow(row);
            written++;
        }

        if (written > 0)
        {
            _csv.Flush();
            Interlocked.Add(ref _totalWritten, written);
            _log.Debug("Wrote {Count} diagnostic row(s); {Pending} pending.", written, _queue.Count);
            StatsChanged?.Invoke();
        }
    }
}
