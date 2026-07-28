using System.IO;
using HarryDataServer.Configuration;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// Collage generator (CLAUDE.md section 12). Driven by the part-exit orchestrator via
/// <see cref="ComposeForPartAsync"/> for OK parts when <c>Collage_Generate=true</c>.
/// Composes from the individual images per Collage.ini and writes a JPG to
/// <c>Collage_ResultImages</c>. Image deletion/backup is handled separately by the
/// orchestrator's image task.
/// </summary>
public sealed class CollageService : ICollageService
{
    private static readonly TimeSpan TransientTtl = TimeSpan.FromMinutes(5);

    private readonly IConfigService _config;
    private readonly ISystemHealth _health;
    private readonly CollageIniReader _reader;
    private readonly CollageComposer _composer;
    private readonly ILogService _log;

    private CollageLayout? _layout;
    private bool _enabled;
    private string _sourceDir = string.Empty;
    private string _outputDir = string.Empty;
    private int _maxFileSizeKb = 128;
    private long _totalGenerated;
    private bool _started;

    public CollageService(
        IConfigService config, ISystemHealth health,
        CollageIniReader reader, CollageComposer composer, ILogService log)
    {
        _config = config;
        _health = health;
        _reader = reader;
        _composer = composer;
        _log = log;
    }

    public int PendingCount => 0;
    public long TotalGenerated => Interlocked.Read(ref _totalGenerated);
    public event Action? StatsChanged;
    public event Action<string, DateTime>? CollageGenerated;

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;
        _started = true;

        var collage = _config.Config.Collage;
        _enabled = collage.Generate;
        _sourceDir = collage.SingleImagesPath;
        _outputDir = collage.ResultImagesPath;
        _maxFileSizeKb = collage.MaxFileSizeKb;

        if (!_enabled)
        {
            _log.Information("Collage generation disabled; service idle.");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_sourceDir) || string.IsNullOrWhiteSpace(_outputDir))
        {
            _enabled = false;
            _log.Warning("Collage: source/output path not configured; collage disabled.");
            return Task.CompletedTask;
        }

        TryLoadLayout(collage.IniPath);
        _log.Information("Collage service ready; layout '{Ini}' ({Count} image(s)) → {Out}.",
            collage.IniPath, _layout?.Images.Count ?? 0, _outputDir);
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Compose the collage for one OK part. Returns true on success or when disabled;
    /// false only on a genuine failure (exception). Missing images log a warning but do
    /// not fail the part (the collage is a best-effort artifact).
    /// </summary>
    public async Task<bool> ComposeForPartAsync(SpsPartExitData part, CancellationToken ct)
    {
        if (!_enabled || _layout is null)
            return true;

        var serials = SearchSerials(part);
        if (serials.Count == 0)
            return true;

        var outputPath = Path.Combine(_outputDir, $"{Sanitize(part.Szid)}_Collage.jpg");

        try
        {
            var result = await Task.Run(() => _composer.Compose(_layout, serials, _sourceDir, outputPath, _maxFileSizeKb), ct)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                _health.Report(HealthSources.Collage, HealthSeverity.Warning,
                    $"No source images found for part {part.Szid}", TransientTtl);
                _log.Warning("Collage: no source images for part {Serial}; nothing written.", part.Szid);
                return true;
            }

            Interlocked.Increment(ref _totalGenerated);
            _health.Clear(HealthSources.Collage);
            _log.Information("Collage written for {Serial}: {Placed} placed, {Missing} missing → {Path}.",
                part.Szid, result.Placed, result.Missing, result.OutputPath);
            StatsChanged?.Invoke();
            if (result.OutputPath is not null)
                CollageGenerated?.Invoke(result.OutputPath, DateTime.Now);
            return true;
        }
        catch (Exception ex)
        {
            _health.Report(HealthSources.Collage, HealthSeverity.Warning,
                $"Collage generation failing: {ex.Message}", TransientTtl);
            _log.Error(ex, "Collage generation failed for part {Serial}.", part.Szid);
            return false;
        }
    }

    private void TryLoadLayout(string iniPath)
    {
        try
        {
            _layout = _reader.Load(iniPath);
            if (!_layout.IsValid)
            {
                _enabled = false;
                _health.Report(HealthSources.Collage, HealthSeverity.Warning,
                    "Collage.ini has no usable canvas/images; collages disabled");
                _log.Warning("Collage: layout '{Ini}' is empty or invalid; service idle.", iniPath);
            }
        }
        catch (Exception ex)
        {
            _enabled = false;
            _health.Report(HealthSources.Collage, HealthSeverity.Warning,
                $"Collage.ini could not be loaded: {ex.Message}");
            _log.Error(ex, "Collage: failed to load layout '{Ini}'; service idle.", iniPath);
        }
    }

    /// <summary>
    /// The part's image search keys: the bare frame SZID and trimmer serial (when present), each
    /// sanitised to the Field 1 form of a real filename (<see cref="SerialNumberHelper.ToImageSearchKey"/>).
    ///
    /// <para>
    /// This used to insert a <c>'_'</c> after character 12 ("formatted serials", a V1 carry-over).
    /// Real filenames written by the Keyence controllers contain <b>no</b> underscore — Field 1 is
    /// the serial right-padded with <c>'0'</c> — so the inserted <c>'_'</c> made every
    /// <c>filename.Contains(serial)</c> test fail: no collage source images were ever found and no
    /// OK-part low-res image was ever cleaned up (live evidence 2026-07-28: searched
    /// <c>270726160321_0031811</c>, file is <c>2707261603210031811000-…</c>).
    /// </para>
    /// </summary>
    public static List<string> SearchSerials(SpsPartExitData part)
    {
        var list = new List<string>(2);
        AddSearchKey(list, part.Szid);
        AddSearchKey(list, part.VirtualSerial);
        return list;
    }

    private static void AddSearchKey(List<string> list, string serial)
    {
        var key = SerialNumberHelper.ToImageSearchKey(serial);
        if (key.Length > 0)
            list.Add(key);
    }

    private static string Sanitize(string serial)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(serial.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
    }
}
