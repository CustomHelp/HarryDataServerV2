using System.Net.Sockets;
using System.Text;
using HarryDataServer.Configuration;
using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.Communication;

/// <summary>
/// TCP client for a single Keyence camera controller (we are always the client,
/// CLAUDE.md sections 3–4). Connects with exponential-backoff reconnect, frames
/// incoming bytes on the carriage-return delimiter, parses each telegram and
/// raises typed events. Runs entirely on a background task — it never blocks the
/// UI thread, and the receive loop never performs DB/file I/O (Phase 4 consumes
/// the events via queues).
/// </summary>
public sealed class TcpCameraClient
{
    private const int BufferSize = 8192;            // TCP buffer (CLAUDE.md section 4)
    private const byte CarriageReturn = 0x0D;       // telegram delimiter '\r'
    private const int MaxAccumulatorBytes = 1 << 20; // 1 MB safety cap against a missing delimiter

    private static readonly TimeSpan ReconnectInitial = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReconnectMax = TimeSpan.FromSeconds(60);

    // Keepalive watchdog (Keyence version request): poll every 500ms; after 2s of
    // silence send "MR,#Version\r" and wait up to 2s for any reply. ANY inbound data
    // (a measurement telegram, the "MR,1.1" version reply, or an "ER" error reply)
    // counts as alive and resets the watchdog. An unanswered ping is a failed ping;
    // 3 consecutive failed pings → offline + reconnect.
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IdleBeforePing = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);
    private const int MaxFailedPings = 3;

    private readonly CameraConfig _config;
    private readonly CameraTemplates _templates;
    private readonly TelegramParser _parser;
    private readonly ILogService _log;
    private readonly ITelegramCapture? _capture;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _connCts;
    private Task? _runTask;
    private CameraConnectionState _state = CameraConnectionState.Disconnected;
    private bool _loggedOffline;   // outage logged once per Connected→Disconnected transition (RunAsync thread only)
    private int _failedPings;
    private long _telegramCount;
    private DateTime _lastPingUtc;

    public TcpCameraClient(CameraConfig config, CameraTemplates templates, TelegramParser parser, ILogService log,
        ITelegramCapture? capture = null)
    {
        _config = config;
        _templates = templates;
        _parser = parser;
        _log = log;
        _capture = capture;
    }

    public string CameraName => _config.CameraName;
    public string Module => _config.Module;
    public string Ip => _config.Ip;
    public int Port => _config.Port;
    public string Endpoint => $"{_config.Ip}:{_config.Port}";

    /// <summary>True when at least one JSON template (result/settings) was loaded for this camera.</summary>
    public bool JsonLoaded => _templates.Result is not null || _templates.Settings is not null;

    /// <summary>True while the connect/reconnect loop is active (auto-reconnect running).</summary>
    public bool AutoReconnectActive => _runTask is not null && _cts is { IsCancellationRequested: false };

    /// <summary>Total telegrams received and dispatched since start.</summary>
    public long TelegramCount => Interlocked.Read(ref _telegramCount);

    /// <summary>Force an immediate reconnect by dropping the current connection (UI button).</summary>
    public void RequestReconnect()
    {
        try { _connCts?.Cancel(); }
        catch { /* connection already being torn down */ }
    }

    public CameraConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value)
                return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public bool IsConnected => _state == CameraConnectionState.Connected;

    /// <summary>UTC timestamp of the last byte received (used by the offline watchdog).</summary>
    public DateTime LastDataUtc { get; private set; }

    /// <summary>
    /// Active keepalive: the Keyence version-variable request sent while the
    /// connection is idle. Confirmed from the production V1 code. A trailing
    /// carriage return is ensured when sending. Set to empty to disable active
    /// probing (passive monitoring only).
    /// </summary>
    public string KeepAliveCommand { get; set; } = "MR,#Version\r";

    public event EventHandler<CameraConnectionState>? StateChanged;
    public event EventHandler<ParsedTelegram>? TelegramReceived;
    public event EventHandler<ResultsTelegramEventArgs>? ResultsReceived;
    public event EventHandler<SettingsTelegramEventArgs>? SettingsReceived;
    public event EventHandler<DiagnosticTelegramEventArgs>? DiagnosticReceived;

    /// <summary>Start the connect/receive loop on a background task.</summary>
    public Task StartAsync(CancellationToken ct)
    {
        if (_runTask is not null)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stop the client and wait for the background loop to finish.</summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = ReconnectInitial;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                State = CameraConnectionState.Connecting;

                using var client = new TcpClient { NoDelay = true };
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                await client.ConnectAsync(_config.Ip, _config.Port, ct).ConfigureAwait(false);

                State = CameraConnectionState.Connected;
                MarkDataReceived();
                backoff = ReconnectInitial;

                // Option 1: one Information line on recovery, plain connect otherwise.
                if (_loggedOffline)
                {
                    _loggedOffline = false;
                    _log.Information("{Camera}: reconnected to {Endpoint}.", CameraName, Endpoint);
                }
                else
                {
                    _log.Information("{Camera}: connected to {Endpoint}.", CameraName, Endpoint);
                }

                await using var stream = client.GetStream();
                await CommunicateAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Debug("{Camera}: connection error: {Message}", CameraName, ex.Message);
            }

            if (ct.IsCancellationRequested)
                break;

            State = CameraConnectionState.Disconnected;

            // Option 1 (CLAUDE.md §3): log the outage as a WARNING only once, on the
            // Connected→Disconnected transition. Subsequent retry attempts for an
            // already-known-offline controller are logged at Debug so an unreachable
            // camera no longer inflates the warning counter (one Warning per outage).
            if (!_loggedOffline)
            {
                _loggedOffline = true;
                _log.Warning("{Camera}: controller unreachable; reconnecting (retry up to every {Seconds:0}s).",
                    CameraName, ReconnectMax.TotalSeconds);
            }
            else
            {
                _log.Debug("{Camera}: still disconnected; reconnecting in {Seconds:0}s.", CameraName, backoff.TotalSeconds);
            }

            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var next = TimeSpan.FromTicks(backoff.Ticks * 2);
            backoff = next > ReconnectMax ? ReconnectMax : next;
        }

        State = CameraConnectionState.Disconnected;
        _log.Information("{Camera}: client stopped.", CameraName);
    }

    private async Task CommunicateAsync(NetworkStream stream, CancellationToken ct)
    {
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _connCts = connCts;
        var token = connCts.Token;

        Task? keepAlive = string.IsNullOrEmpty(KeepAliveCommand)
            ? null
            : KeepAliveLoopAsync(stream, connCts, token);

        try
        {
            await ReceiveLoopAsync(stream, token).ConfigureAwait(false);
        }
        finally
        {
            _connCts = null;
            connCts.Cancel();
            if (keepAlive is not null)
            {
                try { await keepAlive.ConfigureAwait(false); }
                catch { /* keepalive faults are not interesting on teardown */ }
            }
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
            {
                _log.Information("{Camera}: remote closed the connection.", CameraName);
                return;
            }

            MarkDataReceived();
            accumulator.AddRange(buffer[..read]);
            ExtractFrames(accumulator);

            if (accumulator.Count > MaxAccumulatorBytes)
            {
                _log.Warning("{Camera}: no delimiter in {Bytes} bytes; clearing buffer.", CameraName, accumulator.Count);
                accumulator.Clear();
            }
        }
    }

    /// <summary>Pull every complete (delimiter-terminated) telegram out of the accumulator.</summary>
    private void ExtractFrames(List<byte> accumulator)
    {
        int index;
        while ((index = accumulator.IndexOf(CarriageReturn)) >= 0)
        {
            var frame = accumulator.GetRange(0, index).ToArray();
            accumulator.RemoveRange(0, index + 1);

            if (frame.Length == 0)
                continue;

            // Trim CR/LF from both ends so CRLF framing can't leave a leading '\n'
            // on the next frame (which would defeat the keepalive prefix check).
            var text = Encoding.Latin1.GetString(frame).Trim('\r', '\n');
            if (text.Length == 0)
                continue;

            ProcessFrame(text);
        }
    }

    private void ProcessFrame(string text)
    {
        try
        {
            // Keyence command replies ("MR,..." version reply / "ER" error reply) are keepalive
            // traffic, not telegrams. They are excluded from the raw capture (Part B) and never
            // passed to the parser; any inbound data still resets the watchdog.
            var isKeepAlive = _parser.IsKeepAliveReply(text);

            // Raw debug capture (test/commissioning): real telegrams (Results/Settings/Diagnostic,
            // incl. NoSerial bad telegrams) are written exactly as received, before parsing.
            if (!isKeepAlive)
                _capture?.Capture(CameraName, text);

            if (isKeepAlive)
            {
                MarkDataReceived();
                _log.Debug("{Camera}: keepalive reply received: {Reply}.", CameraName, text);
                return;
            }

            var telegram = _parser.ParseLine(text);
            if (telegram is null)
                return;

            Interlocked.Increment(ref _telegramCount);
            TelegramReceived?.Invoke(this, telegram);

            switch (telegram.Signal)
            {
                case TelegramSignal.Results:
                    // NoSerial guard (CLAUDE.md §4): an all-zero/empty Serial1 means the controller
                    // produced a bad telegram. Drop it from the DB pipeline entirely — do not raise
                    // ResultsReceived, so neither MeasurementProcessor nor MsaService writes anything.
                    // It is still captured above and shown as "NoSerial" in the camera control.
                    if (telegram.IsNoSerial)
                    {
                        _log.Warning("{Camera}: Results telegram with no/zero serial (bad telegram); not written to DB.",
                            CameraName);
                        break;
                    }

                    var measurements = _templates.Result is not null
                        ? _parser.ExtractMeasurements(telegram, _templates.Result)
                        : Array.Empty<MeasurementSample>();
                    _log.Debug("{Camera}: Results mode={Mode} serial={Serial} samples={Count}.",
                        CameraName, telegram.Mode, telegram.Serial1, measurements.Count);
                    ResultsReceived?.Invoke(this, new ResultsTelegramEventArgs(telegram, measurements));
                    break;

                case TelegramSignal.Settings:
                    var settings = _templates.Settings is not null
                        ? _parser.ExtractSettings(telegram, _templates.Settings)
                        : Array.Empty<SettingSample>();
                    _log.Information("{Camera}: Settings telegram received ({Count} limits).", CameraName, settings.Count);
                    SettingsReceived?.Invoke(this, new SettingsTelegramEventArgs(telegram, settings));
                    break;

                case TelegramSignal.Diagnostic:
                    _log.Debug("{Camera}: Diagnostic telegram received.", CameraName);
                    DiagnosticReceived?.Invoke(this, new DiagnosticTelegramEventArgs(telegram));
                    break;

                default:
                    // Unknown signal words (e.g. a version/keepalive reply) still prove the camera is alive.
                    _log.Debug("{Camera}: telegram signal '{Signal}' (keepalive/other).", CameraName, telegram.RawSignal);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{Camera}: failed to process telegram: {Text}", CameraName, text);
        }
    }

    /// <summary>
    /// Record inbound activity. ANY data (measurement telegram, MR version reply,
    /// or ER error reply) means the camera is alive: refresh the silence timer,
    /// clear the failed-ping count and cancel any outstanding ping.
    /// </summary>
    private void MarkDataReceived()
    {
        LastDataUtc = DateTime.UtcNow;
        _failedPings = 0;
        _lastPingUtc = default;
    }

    /// <summary>
    /// Watchdog: every 500ms. After 2s of silence send the version request and wait
    /// up to <see cref="PingTimeout"/> for any reply. An unanswered ping increments
    /// the failed-ping count; 3 in a row → offline + reconnect. A send failure also
    /// counts as a failed ping.
    /// </summary>
    private async Task KeepAliveLoopAsync(NetworkStream stream, CancellationTokenSource connCts, CancellationToken token)
    {
        var payload = KeepAliveCommand.EndsWith('\r') ? KeepAliveCommand : KeepAliveCommand + "\r";
        var command = Encoding.Latin1.GetBytes(payload);

        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(WatchdogInterval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var now = DateTime.UtcNow;

            // Camera is actively sending (any data within the idle window) → healthy.
            if (now - LastDataUtc < IdleBeforePing)
                continue;

            // A ping is outstanding and its response window has not elapsed yet → keep waiting.
            if (_lastPingUtc != default && now - _lastPingUtc < PingTimeout)
                continue;

            // The previous ping went unanswered (no data arrived within PingTimeout).
            if (_lastPingUtc != default && RegisterFailedPing("no response", connCts))
                return;

            try
            {
                await stream.WriteAsync(command, token).ConfigureAwait(false);
                _lastPingUtc = DateTime.UtcNow;
                _log.Debug("{Camera}: sent keepalive (version request).", CameraName);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _lastPingUtc = DateTime.UtcNow; // back off a full PingTimeout before retrying
                if (RegisterFailedPing($"send failed: {ex.Message}", connCts))
                    return;
            }
        }
    }

    /// <summary>
    /// Count a failed ping and, once <see cref="MaxFailedPings"/> is reached, mark
    /// the camera offline and trigger a reconnect. Returns true if offline.
    /// </summary>
    private bool RegisterFailedPing(string reason, CancellationTokenSource connCts)
    {
        _failedPings++;
        _log.Debug("{Camera}: failed ping ({Count}/{Max}) — {Reason}.",
            CameraName, _failedPings, MaxFailedPings, reason);

        if (_failedPings < MaxFailedPings)
            return false;

        // The resulting disconnect surfaces the single per-outage WARNING via RunAsync's
        // state-change logging, so this internal detail stays at Debug (no double-counting).
        _log.Debug("{Camera}: {Count} consecutive failed pings; marking offline and reconnecting.",
            CameraName, _failedPings);
        connCts.Cancel();
        return true;
    }
}
