using System.Collections.Concurrent;
using System.Globalization;
using HarryDataServer.Communication;
using HarryDataServer.Infrastructure;

namespace HarryDataServer.Services;

/// <summary>
/// Background diagnostic pipeline. Subscribes to every camera's
/// <see cref="TcpCameraClient.DiagnosticReceived"/> event, enqueues a row (no I/O
/// on the receive thread) and writes it straight to a rotating CSV file — never to
/// the database (CLAUDE.md sections 4 + 13). Same pattern as <see cref="MeasurementProcessor"/>.
/// </summary>
public sealed class DiagnosticProcessor : IDiagnosticProcessor
{
    private const int MaxQueue = 200_000;
    private const int MaxItemsPerFlush = 10_000;

    private static readonly string[] Header =
        { "Timestamp", "Controller", "Version", "Mode", "Serial1", "Serial2", "RawTelegram" };

    private readonly ICameraService _cameras;
    private readonly ILogService _log;
    private readonly bool _enabled;
    private readonly string _path;
    private readonly int _maxRows;
    private readonly TimeSpan _flushInterval;

    private readonly ConcurrentQueue<DiagnosticRow> _queue = new();
    private CsvFileWriter? _csv;
    private CancellationTokenSource? _cts;
    private Task? _flushTask;
    private long _totalWritten;
    private bool _started;

    public DiagnosticProcessor(ICameraService cameras, IConfigService config, ILogService log)
    {
        _cameras = cameras;
        _log = log;

        var csv = config.Config.Csv;
        _enabled = csv.DiagnosticSave && !string.IsNullOrWhiteSpace(csv.DiagnosticPath);
        _path = csv.DiagnosticPath;
        _maxRows = csv.DataSetsPerFile;
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

        _csv = new CsvFileWriter(_path, _maxRows, dateSubfolders: true, _log);
        _csv.Configure(Header, "Diagnostic");

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
        _queue.Enqueue(new DiagnosticRow(
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            t.ControllerName, t.Version, t.Mode.ToString(), t.Serial1, t.Serial2, t.Raw));
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
            _csv.WriteRow(new[]
            {
                row.Timestamp, row.Controller, row.Version, row.Mode, row.Serial1, row.Serial2, row.Raw,
            });
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

    private readonly record struct DiagnosticRow(
        string Timestamp, string Controller, string Version, string Mode,
        string Serial1, string Serial2, string Raw);
}
