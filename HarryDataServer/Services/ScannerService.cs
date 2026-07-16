using System.Net;
using System.Net.Sockets;
using System.Text;
using HarryDataServer.Communication;

namespace HarryDataServer.Services;

/// <summary>
/// Implements the DMC scanner bridge (CLAUDE.md scanner bridge). Runs a TCP server on the fixed
/// scanner port that the handheld scanner (TCP client, 172.29.1.100) connects to; each CR-terminated
/// code is timestamped, pushed into a thread-safe ring buffer (for the Scanner tab), raised via
/// <see cref="ScanReceived"/> and forwarded to the <see cref="CompanionBroadcastServer"/> for
/// rebroadcast to the companion apps.
///
/// A scanner going offline is logged as a single WARNING per Connected→Disconnected transition
/// (subsequent state is Debug), matching the camera-controller convention so an unplugged scanner
/// does not inflate the warning counter. The receive loop only touches the in-memory ring buffer and
/// the (non-blocking) broadcast queue — it never does DB/file I/O.
/// </summary>
public sealed class ScannerService : IScannerService
{
    /// <summary>Expected scanner source IP; a connection from elsewhere is accepted but logged (Warning).</summary>
    private const string ExpectedScannerIp = "172.29.1.100";

    private const int BufferSize = 8192;
    private const byte CarriageReturn = 0x0D;
    private const int MaxAccumulatorBytes = 1 << 20;

    private readonly IConfigService _config;
    private readonly ILogService _log;
    private readonly CompanionBroadcastServer _broadcast;

    private readonly object _ringLock = new();
    private readonly Queue<ScanEntry> _ring = new();

    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private int _scannerConnections;

    public ScannerService(IConfigService config, ILogService log, CompanionBroadcastServer broadcast)
    {
        _config = config;
        _log = log;
        _broadcast = broadcast;
        MaxRows = Math.Max(1, _config.Config.Scanner.MaxScanHistoryRows);
        _broadcast.StatusChanged += () => StatusChanged?.Invoke();
    }

    public int MaxRows { get; }
    public bool IsListening { get; private set; }
    public bool ScannerConnected => Volatile.Read(ref _scannerConnections) > 0;
    public int CompanionClientCount => _broadcast.ClientCount;

    public event Action<ScanEntry>? ScanReceived;
    public event Action? StatusChanged;

    public IReadOnlyList<ScanEntry> RecentScans()
    {
        lock (_ringLock)
            return _ring.ToArray();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_acceptTask is not null)
            return;

        var cfg = _config.Config.Scanner;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Start the companion rebroadcast server first so a scan arriving immediately can be fanned out.
        await _broadcast.StartAsync(cfg.CompanionPort, _cts.Token).ConfigureAwait(false);

        _acceptTask = Task.Run(() => AcceptLoopAsync(cfg.ListenPort, _cts.Token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); }
        catch { /* already stopped */ }

        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        await _broadcast.StopAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(int port, CancellationToken token)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Scanner: failed to listen on port {Port}.", port);
            return;
        }

        IsListening = true;
        StatusChanged?.Invoke();
        _log.Information("Scanner: listening on port {Port}.", port);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = HandleScannerAsync(client, token);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log.Debug("Scanner: accept loop ended: {Message}.", ex.Message);
        }
        finally
        {
            IsListening = false;
            try { _listener.Stop(); }
            catch { /* already stopped */ }
            StatusChanged?.Invoke();
        }
    }

    private async Task HandleScannerAsync(TcpClient client, CancellationToken token)
    {
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        var remoteText = remote?.ToString() ?? "unknown";
        if (remote is not null && !string.Equals(remote.Address.ToString(), ExpectedScannerIp, StringComparison.Ordinal))
            _log.Warning("Scanner: connection from unexpected IP {Remote} (expected {Expected}); accepting.",
                remoteText, ExpectedScannerIp);

        client.NoDelay = true;
        Interlocked.Increment(ref _scannerConnections);
        StatusChanged?.Invoke();
        _log.Information("Scanner: connected from {Remote}.", remoteText);

        try
        {
            await using var stream = client.GetStream();
            await ReceiveLoopAsync(stream, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log.Debug("Scanner: receive error from {Remote}: {Message}.", remoteText, ex.Message);
        }
        finally
        {
            client.Dispose();
            var remaining = Interlocked.Decrement(ref _scannerConnections);
            StatusChanged?.Invoke();

            // One Warning per Connected→Disconnected transition (no other scanner connection open),
            // matching the camera-controller outage convention (CLAUDE.md §4) so an unplugged scanner
            // does not inflate the warning counter.
            if (remaining == 0)
                _log.Warning("Scanner: disconnected from {Remote}; waiting for reconnect.", remoteText);
            else
                _log.Debug("Scanner: connection from {Remote} closed ({Remaining} still open).", remoteText, remaining);
        }
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[BufferSize];
        var accumulator = new List<byte>(BufferSize);

        while (!token.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (read == 0)
                return; // scanner closed the connection

            accumulator.AddRange(buffer[..read]);
            ExtractCodes(accumulator);

            if (accumulator.Count > MaxAccumulatorBytes)
            {
                _log.Warning("Scanner: no delimiter in {Bytes} bytes; clearing buffer.", accumulator.Count);
                accumulator.Clear();
            }
        }
    }

    /// <summary>Pull every complete (CR-terminated) code out of the accumulator; a trailing '\n' is tolerated.</summary>
    private void ExtractCodes(List<byte> accumulator)
    {
        int index;
        while ((index = accumulator.IndexOf(CarriageReturn)) >= 0)
        {
            var frame = accumulator.GetRange(0, index).ToArray();
            accumulator.RemoveRange(0, index + 1);
            if (frame.Length == 0)
                continue;

            var code = Encoding.ASCII.GetString(frame).Trim('\r', '\n', ' ', '\t');
            if (code.Length == 0)
                continue;

            OnCode(code);
        }
    }

    private void OnCode(string code)
    {
        var entry = new ScanEntry(DateTime.Now, code);

        lock (_ringLock)
        {
            _ring.Enqueue(entry);
            while (_ring.Count > MaxRows)
                _ring.Dequeue();
        }

        _log.Information("Scanner: code {Code} received.", code);

        // Fan out to the companion apps (non-blocking) and notify the UI.
        _broadcast.Broadcast(code);
        ScanReceived?.Invoke(entry);
    }
}
