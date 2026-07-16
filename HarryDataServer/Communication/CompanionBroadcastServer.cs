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

        try
        {
            await using var stream = client.GetStream();
            await foreach (var code in entry.Outbox.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var bytes = Encoding.ASCII.GetBytes(code + "\r");
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log.Debug("Companion broadcast: client {Remote} write ended: {Message}.", remote, ex.Message);
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
}
