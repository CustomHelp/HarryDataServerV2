using System.IO;
using HarryDataServer.Infrastructure;

namespace HarryDataServer.Services;

/// <summary>
/// Global, on-demand raw telegram capture for test/commissioning. When enabled, every
/// incoming raw telegram line (exactly as received, before parsing) is appended to a
/// per-controller file in a <c>Capture</c> folder next to the executable. This is a
/// debugging aid to verify token positions on the live line — it is intentionally
/// separate from the Diagnostic-CSV feature and is never persisted/auto-started.
/// </summary>
public interface ITelegramCapture
{
    /// <summary>True while capture is active.</summary>
    bool Enabled { get; }

    /// <summary>Turn capture on or off. Turning off flushes and closes all open files.</summary>
    void SetEnabled(bool enabled);

    /// <summary>
    /// Append one raw telegram line for a controller (no-op when disabled). Called on the
    /// camera receive threads; must be cheap when off and never throw to the caller.
    /// </summary>
    void Capture(string controllerName, string rawLine);
}

/// <summary>
/// File-backed <see cref="ITelegramCapture"/>. Opens one writer per controller lazily on the
/// first captured line and reuses it until capture is turned off. Thread-safe: telegrams arrive
/// on multiple TCP receive threads, so all writer access is serialized under a single lock.
/// </summary>
public sealed class TelegramCaptureService : ITelegramCapture, IDisposable
{
    private readonly ILogService _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _enabled;

    public TelegramCaptureService(ILogService log) => _log = log;

    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled)
                return;
            _enabled = enabled;
            if (!enabled)
                CloseAll();
        }

        _log.Information("Telegram capture {State}.", enabled ? "started" : "stopped");
    }

    public void Capture(string controllerName, string rawLine)
    {
        if (!_enabled)
            return;

        lock (_gate)
        {
            if (!_enabled)
                return;

            try
            {
                var writer = GetWriter(controllerName);
                // Prepend the received timestamp; the raw line is written verbatim after it so the
                // captured token stream lines up as CSV column N+1 = raw token N.
                writer.Write(FileNaming.Stamp(DateTime.Now));
                writer.Write(',');
                writer.WriteLine(rawLine);
            }
            catch (Exception ex)
            {
                // Disable capture on the first write failure (e.g. disk full) so we don't flood the
                // log with one error per telegram. Surface it once.
                _log.Error(ex, "Telegram capture failed for {Controller}; disabling capture.", controllerName);
                _enabled = false;
                CloseAll();
            }
        }
    }

    /// <summary>Get-or-create the writer for a controller (caller holds the lock).</summary>
    private StreamWriter GetWriter(string controllerName)
    {
        if (_writers.TryGetValue(controllerName, out var existing))
            return existing;

        var dir = Path.Combine(AppContext.BaseDirectory, "Capture");
        Directory.CreateDirectory(dir);

        var safeName = Sanitize(controllerName);
        var path = Path.Combine(dir, $"Capture_{safeName}_{FileNaming.Stamp(DateTime.Now)}.csv");
        var writer = new StreamWriter(path, append: true) { AutoFlush = true };
        _writers[controllerName] = writer;
        _log.Information("Telegram capture → {File}", path);
        return writer;
    }

    /// <summary>Flush and close every open writer (caller holds the lock).</summary>
    private void CloseAll()
    {
        foreach (var writer in _writers.Values)
        {
            try { writer.Flush(); writer.Dispose(); }
            catch (Exception ex) { _log.Debug("Telegram capture: error closing file: {Message}", ex.Message); }
        }
        _writers.Clear();
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    public void Dispose()
    {
        lock (_gate)
            CloseAll();
    }
}
