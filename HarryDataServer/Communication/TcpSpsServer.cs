using System.Net;
using System.Net.Sockets;
using System.Text;
using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.Communication;

/// <summary>
/// Hosts the 7 PLC/SPS channels as TCP listeners (CLAUDE.md section 5). Each channel
/// is request/response and line-oriented (telegrams terminated by CR or LF). All
/// channels accept multiple PLC connections and never block each other.
/// </summary>
public sealed class TcpSpsServer : ISpsServer
{
    private const int BufferSize = 8192;
    private const string ResponseTerminator = "\r";
    private const int MaxAccumulatorBytes = 1 << 20;

    private readonly IConfigService _config;
    private readonly ICameraService _cameras;
    private readonly ISystemHealth _health;
    private readonly ILogService _log;

    private readonly List<TcpListener> _listeners = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SpsChannel, int> _connectionsByChannel = new();

    /// <summary>The current PLC connection per MSA channel, so a finished evaluation can be PUSHED
    /// on the same open connection (push model). Newest connection wins; removed on disconnect.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SpsChannel, SpsClientConnection> _msaConnections = new();
    private CancellationTokenSource? _cts;
    private readonly List<Task> _acceptTasks = new();
    private int _listeningChannels;
    private int _activeConnections;

    public TcpSpsServer(IConfigService config, ICameraService cameras, ISystemHealth health, ILogService log)
    {
        _config = config;
        _cameras = cameras;
        _health = health;
        _log = log;
    }

    public bool IsRunning { get; private set; }
    public int ListeningChannels => Volatile.Read(ref _listeningChannels);
    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    public event Action? StatusChanged;
    public event EventHandler<SpsPartExitEventArgs>? PartExitReceived;
    public event Action<SpsChannel, bool, string>? ChannelActivity;
    public Func<string, string, string>? MsaRequestHandler { get; set; }
    public Func<SpsPartExitData, Task<bool>>? PartExitHandler { get; set; }

    public int ConnectionsOn(SpsChannel channel) => _connectionsByChannel.GetValueOrDefault(channel);

    public Task StartAsync(CancellationToken ct)
    {
        if (IsRunning)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        var sps = _config.Config.Sps;

        var channels = new (SpsChannel Channel, int Port)[]
        {
            (SpsChannel.KeepAlive, sps.PortKeepAlive),
            (SpsChannel.PartExit, sps.PortPartExit),
            (SpsChannel.MsaM10, sps.PortMsaM10),
            (SpsChannel.MsaM11, sps.PortMsaM11),
            (SpsChannel.MsaM20, sps.PortMsaM20),
            (SpsChannel.MsaM21, sps.PortMsaM21),
            (SpsChannel.MsaM50, sps.PortMsaM50),
        };

        foreach (var (channel, port) in channels)
            _acceptTasks.Add(Task.Run(() => ListenAsync(channel, port, token), CancellationToken.None));

        IsRunning = true;
        _log.Information("SPS server starting on {Count} channels.", channels.Length);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        lock (_listeners)
        {
            foreach (var listener in _listeners)
            {
                try { listener.Stop(); } catch { /* ignore */ }
            }
        }

        try { await Task.WhenAll(_acceptTasks).ConfigureAwait(false); }
        catch { /* listeners cancelled */ }

        IsRunning = false;
        _log.Information("SPS server stopped.");
    }

    private async Task ListenAsync(SpsChannel channel, int port, CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "SPS {Channel}: failed to listen on port {Port}.", channel, port);
            return;
        }

        lock (_listeners) _listeners.Add(listener);
        Interlocked.Increment(ref _listeningChannels);
        StatusChanged?.Invoke();
        _log.Information("SPS {Channel}: listening on port {Port}.", channel, port);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = HandleClientAsync(channel, client, token);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log.Debug("SPS {Channel}: accept loop ended: {Message}", channel, ex.Message);
        }
        finally
        {
            try { listener.Stop(); } catch { /* ignore */ }
            Interlocked.Decrement(ref _listeningChannels);
            StatusChanged?.Invoke();
        }
    }

    private async Task HandleClientAsync(SpsChannel channel, TcpClient client, CancellationToken token)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        Interlocked.Increment(ref _activeConnections);
        _connectionsByChannel.AddOrUpdate(channel, 1, (_, n) => n + 1);
        StatusChanged?.Invoke();
        _log.Information("SPS {Channel}: client connected from {Remote}.", channel, remote);

        SpsClientConnection? conn = null;
        try
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            conn = new SpsClientConnection(stream);
            // Register MSA connections so a completed evaluation can be pushed here (newest wins).
            if (channel.IsMsaChannel())
                _msaConnections[channel] = conn;
            await ReceiveLoopAsync(channel, conn, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log.Debug("SPS {Channel}: client {Remote} error: {Message}", channel, remote, ex.Message);
        }
        finally
        {
            // Only drop the map entry if it is still THIS connection (a newer one may have replaced it).
            if (channel.IsMsaChannel() && conn is not null)
                _msaConnections.TryRemove(new KeyValuePair<SpsChannel, SpsClientConnection>(channel, conn));
            conn?.Dispose();
            try { client.Dispose(); } catch { /* ignore */ }
            Interlocked.Decrement(ref _activeConnections);
            _connectionsByChannel.AddOrUpdate(channel, 0, (_, n) => Math.Max(0, n - 1));
            StatusChanged?.Invoke();
            _log.Information("SPS {Channel}: client {Remote} disconnected.", channel, remote);
        }
    }

    private async Task ReceiveLoopAsync(SpsChannel channel, SpsClientConnection conn, CancellationToken token)
    {
        var stream = conn.Stream;
        var buffer = new byte[BufferSize];
        var accumulator = new List<byte>(BufferSize);

        while (!token.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
            if (read == 0)
                return;

            accumulator.AddRange(buffer[..read]);

            int index;
            while ((index = IndexOfLineBreak(accumulator)) >= 0)
            {
                var frame = accumulator.GetRange(0, index).ToArray();
                accumulator.RemoveRange(0, index + 1);

                var text = Encoding.Latin1.GetString(frame).Trim('\r', '\n', ' ');
                if (text.Length == 0)
                    continue;

                ChannelActivity?.Invoke(channel, false, text);

                // Channel 2: when an orchestrator is registered, defer the response —
                // run the full part pipeline, then send the V1 ACK.
                if (channel == SpsChannel.PartExit && PartExitHandler is { } partHandler)
                {
                    var ack = await HandlePartExitAckAsync(partHandler, text).ConfigureAwait(false);
                    await WriteAsync(conn, Encoding.Latin1.GetBytes(ack), token).ConfigureAwait(false);
                    ChannelActivity?.Invoke(channel, true, ack.TrimEnd('\r', '\n'));
                    continue;
                }

                var response = Dispatch(channel, text);
                if (response is not null)
                {
                    var bytes = Encoding.Latin1.GetBytes(response + ResponseTerminator);
                    await WriteAsync(conn, bytes, token).ConfigureAwait(false);
                    ChannelActivity?.Invoke(channel, true, response);
                }
            }

            if (accumulator.Count > MaxAccumulatorBytes)
            {
                _log.Warning("SPS {Channel}: no delimiter in {Bytes} bytes; clearing buffer.", channel, accumulator.Count);
                accumulator.Clear();
            }
        }
    }

    /// <summary>
    /// Push a completed MSA result to the PLC on the module's channel without a fresh request
    /// (push model, CLAUDE.md §5). Writes on the SAME connection that answered "Wait"; serialised
    /// with the receive-loop responses via the per-connection write lock. No-op (logged) when the
    /// channel has no open connection (e.g. the PLC closed it — it must stay open to receive this).
    /// </summary>
    public async Task<bool> PushMsaResultAsync(string moduleKey, string baseId, string status, CancellationToken ct = default)
    {
        var channel = SpsChannelExtensions.MsaChannelForModule(moduleKey);
        if (channel is null)
        {
            _log.Warning("SPS: cannot push MSA result — unknown module '{Module}'.", moduleKey);
            return false;
        }

        if (!_msaConnections.TryGetValue(channel.Value, out var conn))
        {
            _log.Warning("SPS {Channel}: cannot push MSA result {Status} for {Base} — no open PLC connection (it must stay open to receive the push).",
                channel.Value, status, baseId);
            return false;
        }

        var response = WithBaseId(status, baseId);
        try
        {
            await WriteAsync(conn, Encoding.Latin1.GetBytes(response + ResponseTerminator), ct).ConfigureAwait(false);
            ChannelActivity?.Invoke(channel.Value, true, response);
            _log.Information("SPS {Channel}: pushed MSA result '{Response}' (unsolicited).", channel.Value, response);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning("SPS {Channel}: failed to push MSA result for {Base}: {Message}", channel.Value, baseId, ex.Message);
            return false;
        }
    }

    /// <summary>Serialise all writes to one connection (receive-loop responses + unsolicited pushes).</summary>
    private static async Task WriteAsync(SpsClientConnection conn, byte[] bytes, CancellationToken token)
    {
        await conn.WriteLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await conn.Stream.WriteAsync(bytes, token).ConfigureAwait(false);
        }
        finally
        {
            conn.WriteLock.Release();
        }
    }

    /// <summary>One PLC connection: its stream plus a write lock so responses and pushes never interleave.</summary>
    private sealed class SpsClientConnection : IDisposable
    {
        public SpsClientConnection(NetworkStream stream) => Stream = stream;
        public NetworkStream Stream { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public void Dispose() => WriteLock.Dispose();
    }

    /// <summary>Compute the response for one received telegram on a channel.</summary>
    private string? Dispatch(SpsChannel channel, string telegram)
    {
        try
        {
            return channel switch
            {
                SpsChannel.KeepAlive => BuildKeepAliveResponse(telegram),
                SpsChannel.PartExit => HandlePartExit(telegram),
                _ when channel.IsMsaChannel() => HandleMsaRequest(channel, telegram),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _log.Error(ex, "SPS {Channel}: error handling telegram '{Telegram}'.", channel, telegram);
            return $"Error;{ex.Message}";
        }
    }

    /// <summary>
    /// Channel 1: mirror the received telegram, append the per-camera status string
    /// (one 1/0 per camera, in INI order), then a health signal word and — only when
    /// not healthy — a plain-English fault description:
    ///   healthy:  &lt;mirror&gt;;1;1;0;1;...;OK
    ///   warning:  &lt;mirror&gt;;1;1;0;1;...;WARNING;&lt;text&gt;
    ///   error:    &lt;mirror&gt;;1;1;0;1;...;ERROR;&lt;text&gt;
    /// The camera 1/0 list keeps offline cameras visible without flipping the signal
    /// word; the signal word reflects DB / pipeline faults (CLAUDE.md section 5).
    /// </summary>
    private string BuildKeepAliveResponse(string telegram)
    {
        var status = string.Join(';', _cameras.Clients.Select(c => c.IsConnected ? "1" : "0"));
        var health = _health.Snapshot();

        if (health.IsHealthy)
            return $"{telegram};{status};{health.SignalWord}";

        return $"{telegram};{status};{health.SignalWord};{SanitizeForWire(health.Message)}";
    }

    /// <summary>
    /// Strip delimiters/line breaks from a fault message so it cannot break the
    /// semicolon-framed telegram the PLC parses (the message is the trailing field).
    /// </summary>
    private static string SanitizeForWire(string message) =>
        message.Replace('\r', ' ').Replace('\n', ' ').Replace(';', ',').Trim();

    private string HandlePartExit(string telegram)
    {
        var data = SpsPartExitData.TryParse(telegram);
        if (data is null)
        {
            _log.Warning("SPS PartExit: malformed telegram '{Telegram}'.", telegram);
            return "Error;invalid part exit telegram";
        }

        _log.Information("Part Exit: DMC={Dmc} SZID={Szid} order={Order} mode={Mode} result={Result}.",
            data.Dmc, data.Szid, data.OrderName, data.Mode, data.Result);

        PartExitReceived?.Invoke(this, new SpsPartExitEventArgs(data));
        return "OK";
    }

    /// <summary>
    /// Run the registered part-exit orchestrator and format the ACK:
    /// <c>serial.PadRight(32,'0') + ";" + true|false + "\r"</c>.
    /// The terminator is a single CR — consistent with every other SPS channel
    /// (agreed with the PLC programmer 2026-07-02; V1 used CR+LF).
    /// </summary>
    private async Task<string> HandlePartExitAckAsync(Func<SpsPartExitData, Task<bool>> handler, string telegram)
    {
        var data = SpsPartExitData.TryParse(telegram);
        if (data is null)
        {
            _log.Warning("SPS PartExit: malformed telegram '{Telegram}'.", telegram);
            return new string('0', 32) + ";false\r";
        }

        _log.Information("Part Exit: DMC={Dmc} SZID={Szid} order={Order} mode={Mode} result={Result}.",
            data.Dmc, data.Szid, data.OrderName, data.Mode, data.Result);

        bool success;
        try
        {
            success = await handler(data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Part exit orchestration failed for {Serial}.", data.Szid);
            success = false;
        }

        var serial = (data.Szid ?? string.Empty).PadRight(32, '0');
        return $"{serial};{(success ? "true" : "false")}\r";
    }

    /// <summary>
    /// Channels 3–7: "Request;&lt;BaseID&gt;" → the requested BaseID is mirrored back as
    /// field 1 of the response: "&lt;status&gt;;&lt;BaseID&gt;[;&lt;desc&gt;]" (Wait / OK / NG /
    /// Error). The PLC can thus correlate each poll response with its request. The BaseID
    /// field is empty when the request format was invalid (no parsable BaseID).
    /// </summary>
    private string HandleMsaRequest(SpsChannel channel, string telegram)
    {
        var parts = telegram.Split(';');
        if (parts.Length < 2 || !string.Equals(parts[0].Trim(), "Request", StringComparison.OrdinalIgnoreCase))
            return WithBaseId("Error;expected 'Request;<BaseID>'", string.Empty);

        var baseId = parts[1].Trim();
        var handler = MsaRequestHandler;
        var status = handler is null
            ? "Wait" // MSA engine not active yet (Phase 10).
            : handler(channel.ModuleKey(), baseId) ?? "Wait";

        return WithBaseId(status, baseId);
    }

    /// <summary>
    /// Insert the request BaseID as field 1 of an MSA status response:
    /// "Wait" → "Wait;&lt;BaseID&gt;"; "Error;&lt;desc&gt;" → "Error;&lt;BaseID&gt;;&lt;desc&gt;".
    /// </summary>
    private static string WithBaseId(string status, string baseId)
    {
        var split = status.IndexOf(';');
        return split < 0
            ? $"{status};{baseId}"
            : $"{status[..split]};{baseId}{status[split..]}";
    }

    private static int IndexOfLineBreak(List<byte> buffer)
    {
        for (var i = 0; i < buffer.Count; i++)
        {
            if (buffer[i] == (byte)'\r' || buffer[i] == (byte)'\n')
                return i;
        }
        return -1;
    }
}
