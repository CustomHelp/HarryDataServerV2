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

    // Keepalive watchdog (Keyence version request): probe after 2s of silence,
    // poll every 500ms, declare offline after 10s of silence or 3 failed pings.
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IdleBeforePing = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxSilence = TimeSpan.FromSeconds(10);
    private const int MaxFailedPings = 3;

    // Prefixes of Keyence command replies (version response / error response):
    // these are keepalive traffic, not measurement telegrams.
    private const string VersionReplyPrefix = "MR";
    private const string ErrorReplyPrefix = "ER";

    private readonly CameraConfig _config;
    private readonly CameraTemplates _templates;
    private readonly TelegramParser _parser;
    private readonly ILogService _log;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private CameraConnectionState _state = CameraConnectionState.Disconnected;
    private int _failedPings;

    public TcpCameraClient(CameraConfig config, CameraTemplates templates, TelegramParser parser, ILogService log)
    {
        _config = config;
        _templates = templates;
        _parser = parser;
        _log = log;
    }

    public string CameraName => _config.CameraName;
    public string Endpoint => $"{_config.Ip}:{_config.Port}";

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
                _log.Information("{Camera}: connected to {Endpoint}.", CameraName, Endpoint);

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
            _log.Warning("{Camera}: disconnected; reconnecting in {Seconds:0}s.", CameraName, backoff.TotalSeconds);

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

            var text = Encoding.Latin1.GetString(frame).TrimEnd('\n');
            ProcessFrame(text);
        }
    }

    private void ProcessFrame(string text)
    {
        try
        {
            // Keyence command replies (version response "MR..." / error "ER...") are
            // keepalive traffic — reset the watchdog and skip the measurement pipeline.
            // Measurement telegrams start with the controller name (e.g. "M50_..."),
            // so they never collide with these prefixes.
            if (text.StartsWith(VersionReplyPrefix, StringComparison.Ordinal) ||
                text.StartsWith(ErrorReplyPrefix, StringComparison.Ordinal))
            {
                MarkDataReceived();
                _log.Debug("{Camera}: keepalive reply received ({Prefix}).",
                    CameraName, text.Length >= 2 ? text[..2] : text);
                return;
            }

            var telegram = _parser.ParseLine(text);
            if (telegram is null)
                return;

            TelegramReceived?.Invoke(this, telegram);

            switch (telegram.Signal)
            {
                case TelegramSignal.Results:
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

    /// <summary>Record inbound activity: refresh the silence timer and clear the failed-ping count.</summary>
    private void MarkDataReceived()
    {
        LastDataUtc = DateTime.UtcNow;
        _failedPings = 0;
    }

    /// <summary>
    /// Watchdog: every 500ms, after 2s of silence send the Keyence version request.
    /// Force a reconnect after 10s of total silence or 3 consecutive failed pings.
    /// </summary>
    private async Task KeepAliveLoopAsync(NetworkStream stream, CancellationTokenSource connCts, CancellationToken token)
    {
        var payload = KeepAliveCommand.EndsWith('\r') ? KeepAliveCommand : KeepAliveCommand + "\r";
        var command = Encoding.Latin1.GetBytes(payload);

        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(WatchdogInterval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var silence = DateTime.UtcNow - LastDataUtc;

            if (silence > MaxSilence)
            {
                _log.Warning("{Camera}: no data for {Seconds:0.0}s; forcing reconnect.",
                    CameraName, silence.TotalSeconds);
                connCts.Cancel();
                return;
            }

            if (silence < IdleBeforePing)
                continue; // Camera is actively sending; no probe needed.

            try
            {
                await stream.WriteAsync(command, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _failedPings++;
                _log.Debug("{Camera}: keepalive send failed ({Count}/{Max}): {Message}",
                    CameraName, _failedPings, MaxFailedPings, ex.Message);

                if (_failedPings >= MaxFailedPings)
                {
                    _log.Warning("{Camera}: {Count} consecutive failed pings; forcing reconnect.",
                        CameraName, _failedPings);
                    connCts.Cancel();
                    return;
                }
            }
        }
    }
}
