using System.Collections.Concurrent;
using System.IO;
using HarryDataServer.Configuration;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// Phase 9 collage generator. On Part Exit = OK (production parts only) it queues the
/// part, composes a collage from its individual camera images per Collage.ini, writes
/// it to the NAS collage input folder, and — if configured — deletes the now-consumed
/// OK source images (CLAUDE.md sections 11–12). All work happens on a background task.
/// </summary>
public sealed class CollageService : ICollageService
{
    private const int MaxQueue = 50_000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TransientTtl = TimeSpan.FromMinutes(5);

    private readonly IConfigService _config;
    private readonly ISpsServer _sps;
    private readonly ISystemHealth _health;
    private readonly CollageIniReader _reader;
    private readonly CollageComposer _composer;
    private readonly ILogService _log;

    private readonly ConcurrentQueue<SpsPartExitData> _queue = new();

    private CollageLayout? _layout;
    private bool _enabled;
    private string _sourceDir = string.Empty;
    private string _outputDir = string.Empty;
    private bool _deleteAfter;

    private CancellationTokenSource? _cts;
    private Task? _task;
    private long _totalGenerated;
    private bool _started;

    public CollageService(
        IConfigService config,
        ISpsServer sps,
        ISystemHealth health,
        CollageIniReader reader,
        CollageComposer composer,
        ILogService log)
    {
        _config = config;
        _sps = sps;
        _health = health;
        _reader = reader;
        _composer = composer;
        _log = log;
    }

    public int PendingCount => _queue.Count;
    public long TotalGenerated => Interlocked.Read(ref _totalGenerated);
    public event Action? StatsChanged;

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;

        var collage = _config.Config.Collage;
        var nas = _config.Config.Nas;
        _sourceDir = nas.LowResIndividualPath;
        _outputDir = nas.CollagePath;
        _deleteAfter = nas.DeleteAfterCollage;

        if (!collage.Generate)
        {
            _log.Information("Collage generation disabled; service idle.");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_sourceDir) || string.IsNullOrWhiteSpace(_outputDir))
        {
            _log.Warning("Collage: source or output path not configured; service idle.");
            return Task.CompletedTask;
        }

        if (!TryLoadLayout(collage.IniPath))
            return Task.CompletedTask;

        _enabled = true;
        _sps.PartExitReceived += OnPartExitReceived;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _task = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _log.Information("Collage service started; layout '{Ini}' ({Count} image(s)) → {Out}.",
            collage.IniPath, _layout!.Images.Count, _outputDir);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_enabled)
            return;

        _sps.PartExitReceived -= OnPartExitReceived;
        _cts?.Cancel();
        if (_task is not null)
        {
            try { await _task.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    private bool TryLoadLayout(string iniPath)
    {
        try
        {
            _layout = _reader.Load(iniPath);
            if (!_layout.IsValid)
            {
                _health.Report(HealthSources.Collage, HealthSeverity.Warning,
                    "Collage.ini has no usable canvas/images; collages disabled");
                _log.Warning("Collage: layout '{Ini}' is empty or invalid; service idle.", iniPath);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _health.Report(HealthSources.Collage, HealthSeverity.Warning,
                $"Collage.ini could not be loaded: {ex.Message}");
            _log.Error(ex, "Collage: failed to load layout '{Ini}'; service idle.", iniPath);
            return false;
        }
    }

    // --- Receive side (SPS thread; in-memory only) ---

    private void OnPartExitReceived(object? sender, SpsPartExitEventArgs e)
    {
        var data = e.Data;

        // Collages are produced for good production parts only (not NG, not MSA runs).
        if (data.Result != PartResult.Ok || data.IsMsa)
            return;

        if (_queue.Count >= MaxQueue)
        {
            _health.Report(HealthSources.Collage, HealthSeverity.Warning,
                $"Collage queue full ({MaxQueue}); some collages skipped", TransientTtl);
            _log.Warning("Collage queue full ({Max}); skipping part {Serial}.", MaxQueue, data.Szid);
            return;
        }

        _queue.Enqueue(data);
    }

    // --- Compose side (dedicated background task; all file/image I/O) ---

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            while (!ct.IsCancellationRequested && _queue.TryDequeue(out var part))
                ProcessPart(part);
        }
    }

    private void ProcessPart(SpsPartExitData part)
    {
        if (_layout is null || string.IsNullOrWhiteSpace(part.Szid))
            return;

        var prefixes = SerialPrefixes(part);
        if (prefixes.Count == 0)
            return;

        var outputPath = Path.Combine(_outputDir, $"{Sanitize(part.Szid)}_collage.png");

        try
        {
            var result = _composer.Compose(_layout, prefixes, _sourceDir, outputPath);

            if (!result.Success)
            {
                _health.Report(HealthSources.Collage, HealthSeverity.Warning,
                    $"No source images found for part {part.Szid}", TransientTtl);
                _log.Warning("Collage: no source images for part {Serial}; nothing written.", part.Szid);
                return;
            }

            Interlocked.Increment(ref _totalGenerated);
            _health.Clear(HealthSources.Collage);
            _log.Information("Collage written for {Serial}: {Placed} placed, {Missing} missing → {Path}.",
                part.Szid, result.Placed, result.Missing, result.OutputPath);
            StatsChanged?.Invoke();

            if (_deleteAfter)
                DeleteSources(result.UsedSourceFiles, part.Szid);
        }
        catch (Exception ex)
        {
            _health.Report(HealthSources.Collage, HealthSeverity.Warning,
                $"Collage generation failing: {ex.Message}", TransientTtl);
            _log.Error(ex, "Collage generation failed for part {Serial}.", part.Szid);
        }
    }

    /// <summary>12-char search prefixes: SZID, plus the trimmer serial when present.</summary>
    private static List<string> SerialPrefixes(SpsPartExitData part)
    {
        var prefixes = new List<string>(2);
        if (part.Szid.Length >= 12)
            prefixes.Add(part.Szid[..12]);
        if (part.VirtualSerial.Length >= 12)
            prefixes.Add(part.VirtualSerial[..12]);
        return prefixes;
    }

    private void DeleteSources(IReadOnlyList<string> files, string serial)
    {
        var deleted = 0;
        foreach (var file in files)
        {
            try { File.Delete(file); deleted++; }
            catch (Exception ex) { _log.Debug("Collage: could not delete source {File}: {Message}", file, ex.Message); }
        }

        if (deleted > 0)
            _log.Information("Collage: deleted {Count} consumed OK image(s) for part {Serial}.", deleted, serial);
    }

    private static string Sanitize(string serial)
    {
        Span<char> buffer = stackalloc char[serial.Length];
        var invalid = Path.GetInvalidFileNameChars();
        for (var i = 0; i < serial.Length; i++)
            buffer[i] = Array.IndexOf(invalid, serial[i]) >= 0 ? '_' : serial[i];
        return new string(buffer);
    }
}
