using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using HarryDataServer.Services;

namespace HarryDataServer.Communication;

/// <summary>
/// TCP server that rebroadcasts DMC scan codes to the companion apps (HarryAnalysis,
/// HarryLimitSample). The scanner hardware can only connect to a single server, so the scanner
/// listener forwards each received code here and this server fans it out — raw code, CR-terminated —
/// to every currently connected companion client (scanner-bridge feature).
///
/// Each accepted client gets its own bounded outbox <see cref="Channel{T}"/> drained by a dedicated
/// writer task, so a slow or dead companion socket only backs up (and eventually drops) its own
/// queue — it can never stall <see cref="Broadcast"/> or, transitively, ingestion from the scanner
/// (the "no blocking of the scanner-receive thread" requirement). A write failure removes the
/// client. Client connect/disconnect is logged at Information (routine — companions reconnect on
/// every server restart, so it must not inflate the warning counter); bind failure is an Error.
/// </summary>
public sealed class CompanionBroadcastServer
{
    private const int OutboxCapacity = 256;

    private sealed class ClientEntry
    {
        public required TcpClient Tcp { get; init; }
        public required Channel<string> Outbox { get; init; }
        public required string Remote { get; init; }
    }

    private readonly ILogService _log;
    private readonly ConcurrentDictionary<Guid, ClientEntry> _clients = new();

    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptTask;

    public CompanionBroadcastServer(ILogService log) => _log = log;

    /// <summary>Number of companion clients currently connected.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>True once the listener is bound and accepting.</summary>
    public bool IsListening { get; private set; }

    /// <summary>Raised when a client connects/disconnects (for UI status refresh).</summary>
    public event Action? StatusChanged;

    /// <summary>Start the broadcast listener on <paramref name="port"/> (idempotent).</summary>
    public Task StartAsync(int port, CancellationToken ct)
    {
        if (_acceptTask is not null)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptTask = Task.Run(() => AcceptLoopAsync(port, _cts.Token), CancellationToken.None);
        return Task.CompletedTask;
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
    }

    /// <summary>
    /// Queue a raw scan code (CR-terminated on the wire) to every connected companion. Non-blocking:
    /// each code is offered to each client's bounded outbox; if a client's queue is full the oldest
    /// entry is dropped so ingestion never stalls on a stuck socket.
    /// </summary>
    public void Broadcast(string code)
    {
        foreach (var entry in _clients.Values)
        {
            // Bounded channel with DropOldest: TryWrite always succeeds and evicts the stalest code
            // for a client that isn't draining, so no unbounded growth and no blocking.
            entry.Outbox.Writer.TryWrite(code);
        }
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
            _log.Error(ex, "Companion broadcast: failed to listen on port {Port}.", port);
            return;
        }

        IsListening = true;
        StatusChanged?.Invoke();
        _log.Information("Companion broadcast: listening on port {Port}.", port);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = HandleClientAsync(client, token);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log.Debug("Companion broadcast: accept loop ended: {Message}.", ex.Message);
        }
        finally
        {
            IsListening = false;
            try { _listener.Stop(); }
            catch { /* already stopped */ }
            StatusChanged?.Invoke();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var entry = new ClientEntry
        {
            Tcp = client,
            Remote = remote,
            Outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(OutboxCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }),
        };

        client.NoDelay = true;
        _clients[id] = entry;
        StatusChanged?.Invoke();
        _log.Information("Companion broadcast: client connected from {Remote} ({Count} total).", remote, _clients.Count);

        // Run the outbox writer and a close-detector concurrently. Companions never send data, so
        // without a read the server would only notice a closed client on the next write — meaning a
        // closed app lingers in the list (and the "N connected" count) until the next scan. The
        // detector's ReadAsync returns 0 on the client's FIN, so a closed app is dropped within ~1s.
        using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            await using var stream = client.GetStream();
            var writeTask = WriteLoopAsync(stream, entry.Outbox.Reader, clientCts.Token);
            var closeTask = DetectCloseAsync(stream, clientCts.Token);

            await Task.WhenAny(writeTask, closeTask).ConfigureAwait(false);
            clientCts.Cancel(); // whichever finished first, tear the other down

            try { await Task.WhenAll(writeTask, closeTask).ConfigureAwait(false); }
            catch { /* the loser throws OperationCanceledException on teardown — expected */ }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log.Debug("Companion broadcast: client {Remote} ended: {Message}.", remote, ex.Message);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            entry.Outbox.Writer.TryComplete();
            try { client.Dispose(); }
            catch { /* best effort */ }
            StatusChanged?.Invoke();
            _log.Information("Companion broadcast: client {Remote} disconnected ({Count} remaining).", remote, _clients.Count);
        }
    }

    /// <summary>Drain the client's outbox to its socket, CR-terminated.</summary>
    private static async Task WriteLoopAsync(NetworkStream stream, ChannelReader<string> reader, CancellationToken ct)
    {
        await foreach (var code in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var bytes = Encoding.ASCII.GetBytes(code + "\r");
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Detect a closed client: companions are not expected to send anything, so any received bytes
    /// are discarded — a 0-byte read (FIN) or an exception means the client is gone and this returns,
    /// which tears down the connection and removes it from the list.
    /// </summary>
    private static async Task DetectCloseAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[256];
        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                return; // client closed the connection (FIN)
        }
    }
}
