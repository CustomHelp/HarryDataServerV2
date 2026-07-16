using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HarryShared.Communication
{
    /// <summary>
    /// TCP client used by the companion tools (HarryAnalysis, HarryLimitSample) to receive DMC scan
    /// codes rebroadcast by HarryDataServer's companion broadcast server (default 172.29.1.5:9000).
    /// The scanner hardware can only connect to a single server, so the server fans each scan out to
    /// every connected companion; this client is the receiving end.
    ///
    /// Connects with exponential-backoff reconnect (3s → 60s, doubling) — the same strategy the
    /// server uses for the Keyence camera clients — and frames incoming bytes on the carriage-return
    /// delimiter (a trailing '\n' is tolerated). Runs entirely on a background task and surfaces two
    /// events: <see cref="CodeReceived"/> (one per scan) and <see cref="ConnectionChanged"/> (for the
    /// status LED). It performs NO logging: the companion tools have no logger — connection state is
    /// reported purely via the event so the host can drive a status LED.
    /// </summary>
    public sealed class ScannerCompanionClient
    {
        private const int BufferSize = 8192;
        private const byte CarriageReturn = 0x0D;
        private const int MaxAccumulatorBytes = 1 << 20;

        private static readonly TimeSpan ReconnectInitial = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ReconnectMax = TimeSpan.FromSeconds(60);

        private readonly string _host;
        private readonly int _port;

        private CancellationTokenSource? _cts;
        private Task? _runTask;
        private bool _connected;

        public ScannerCompanionClient(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public string Host => _host;
        public int Port => _port;
        public string Endpoint => $"{_host}:{_port}";

        /// <summary>True while the socket to the companion broadcast server is connected.</summary>
        public bool IsConnected => _connected;

        /// <summary>Raised (background thread) with the raw scan code for each received line.</summary>
        public event Action<string>? CodeReceived;

        /// <summary>Raised (background thread) when the connection state flips; true = connected.</summary>
        public event Action<bool>? ConnectionChanged;

        /// <summary>Start the connect/receive loop on a background task (idempotent).</summary>
        public void Start()
        {
            if (_runTask is not null)
                return;
            _cts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunAsync(_cts.Token));
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
                    using var client = new TcpClient { NoDelay = true };
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);

                    SetConnected(true);
                    backoff = ReconnectInitial;

                    await using var stream = client.GetStream();
                    await ReceiveLoopAsync(stream, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    /* connection error — reconnect after backoff (no logger in companion tools) */
                }

                SetConnected(false);

                if (ct.IsCancellationRequested)
                    break;

                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                var next = TimeSpan.FromTicks(backoff.Ticks * 2);
                backoff = next > ReconnectMax ? ReconnectMax : next;
            }

            SetConnected(false);
        }

        private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[BufferSize];
            var accumulator = new System.Collections.Generic.List<byte>(BufferSize);

            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0)
                    return; // remote closed

                accumulator.AddRange(buffer[..read]);
                ExtractFrames(accumulator);

                if (accumulator.Count > MaxAccumulatorBytes)
                    accumulator.Clear();
            }
        }

        private void ExtractFrames(System.Collections.Generic.List<byte> accumulator)
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

                CodeReceived?.Invoke(code);
            }
        }

        private void SetConnected(bool value)
        {
            if (_connected == value)
                return;
            _connected = value;
            ConnectionChanged?.Invoke(value);
        }
    }
}
