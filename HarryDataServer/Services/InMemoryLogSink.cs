using Serilog.Core;
using Serilog.Events;

namespace HarryDataServer.Services;

/// <summary>
/// Serilog sink that keeps the most recent log lines in memory for the UI Log tab
/// (no file re-reading). Bounded ring buffer; thread-safe.
/// </summary>
public sealed class InMemoryLogSink : ILogBuffer, ILogEventSink
{
    private const int Capacity = 1000;

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new(Capacity);
    private int _errorCount;
    private int _warningCount;

    public int ErrorCount => Volatile.Read(ref _errorCount);
    public int WarningCount => Volatile.Read(ref _warningCount);
    public event Action? Changed;

    public void Emit(LogEvent logEvent)
    {
        var line = $"{logEvent.Timestamp:HH:mm:ss.fff} [{Abbrev(logEvent.Level)}] {logEvent.RenderMessage()}";
        if (logEvent.Exception is not null)
            line += " | " + logEvent.Exception.Message;

        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Capacity)
                _lines.Dequeue();
        }

        if (logEvent.Level >= LogEventLevel.Error)
            Interlocked.Increment(ref _errorCount);
        else if (logEvent.Level == LogEventLevel.Warning)
            Interlocked.Increment(ref _warningCount);

        Changed?.Invoke();
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return _lines.ToArray();
    }

    private static string Abbrev(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "???",
    };
}
