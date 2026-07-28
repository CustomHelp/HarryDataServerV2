using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.Infrastructure;

/// <summary>
/// Per-part fill rule for the production CSV's <b>merged</b> columns (2026-07-28, see
/// <see cref="CsvColumnLayout"/>).
///
/// <para><b>Value rule:</b> a shared column takes the <b>non-empty</b> value of its source controllers.
/// The sources are mutually exclusive per part (strand A = M10+M20, strand B = M11+M21; M50 ST110
/// window KF1 vs KF3) — verified over 4 099 live rows and 1 099 036 filled cells without a single
/// collision.</para>
///
/// <para><b>Collision rule:</b> should both sources ever be filled, the controller that <b>matches the
/// part</b> wins — taken from <c>M1xModule</c>/<c>M2xModule</c> of the part-exit telegram — and exactly
/// <b>one WARNING per part</b> is logged (not per cell). The M50 ST110 windows have no counterpart in the
/// telegram, so there the first non-empty value wins and <see cref="M50St110Kf"/> records which window
/// it came from.</para>
///
/// <para>For an <b>unmerged</b> column this degrades to the plain assignment it always was: the first
/// (and only) controller owns the cell, and its R_ and V_ halves may both write it.</para>
/// </summary>
public sealed class CsvMergeFill
{
    private readonly string? _preferredM1x;
    private readonly string? _preferredM2x;
    private readonly ILogService _log;
    private readonly Dictionary<int, string> _writtenBy = new();
    private readonly List<string> _conflicts = new();

    public CsvMergeFill(SpsPartExitData part, ILogService log)
    {
        _log = log;
        _preferredM1x = part.M1xModule is 10 or 11 ? $"M{part.M1xModule}_" : null;
        _preferredM2x = part.M2xModule is 20 or 21 ? $"M{part.M2xModule}_" : null;
    }

    /// <summary>Which M50 ST110 control window supplied the part's ST110 values ("1"/"3"); null if none.</summary>
    public string? M50St110Kf { get; private set; }

    /// <summary>Number of shared columns where both sources were filled (0 in normal operation).</summary>
    public int ConflictCount => _conflicts.Count;

    /// <summary>
    /// Write one measurement cell into <paramref name="row"/> following the rules above.
    /// </summary>
    /// <param name="metaOffset">Number of fixed meta columns in front of the measurement columns.</param>
    /// <param name="column">Measurement column index from <see cref="CsvColumnLayout"/>.</param>
    /// <param name="camera">The controller that produced the value.</param>
    public void Write(string?[] row, int metaOffset, int column, string camera, string cell)
    {
        var index = metaOffset + column;
        var occupied = !string.IsNullOrEmpty(row[index]);

        if (!occupied || !_writtenBy.TryGetValue(column, out var owner))
        {
            _writtenBy[column] = camera;
            row[index] = cell;
            return;
        }

        if (string.Equals(owner, camera, StringComparison.OrdinalIgnoreCase))
        {
            row[index] = cell;   // same controller writing again (its R_/V_ halves) — no collision
            return;
        }

        _conflicts.Add($"{owner}+{camera}");

        var preferred = Preferred(camera);
        if (preferred is null || !camera.StartsWith(preferred, StringComparison.OrdinalIgnoreCase))
            return;              // no part-side hint, or this is not the matching controller → keep

        _writtenBy[column] = camera;
        row[index] = cell;
    }

    /// <summary>
    /// Note the M50 ST110 control window a value came from (the first one wins, see the class doc).
    /// Called for every measurement row, regardless of the controller.
    /// </summary>
    public void NoteController(string camera)
    {
        if (M50St110Kf is not null)
            return;
        if (camera.Equals("M50_ST110_KF1", StringComparison.OrdinalIgnoreCase))
            M50St110Kf = "1";
        else if (camera.Equals("M50_ST110_KF3", StringComparison.OrdinalIgnoreCase))
            M50St110Kf = "3";
    }

    /// <summary>One WARNING per part (never per cell) when a shared column had both sources filled.</summary>
    public void ReportConflicts(SpsPartExitData part)
    {
        if (_conflicts.Count == 0)
            return;

        _log.Warning("Part-exit CSV: {Count} merged column(s) had BOTH sources filled for {Serial} " +
                     "({Pairs}) — kept the controller matching the part (M1x={M1x}, M2x={M2x}); " +
                     "the strands / ST110 windows are expected to be mutually exclusive.",
            _conflicts.Count, part.Szid,
            string.Join(", ", _conflicts.Distinct().Take(5)), part.M1xModule, part.M2xModule);
    }

    private string? Preferred(string camera) =>
        camera.StartsWith("M1", StringComparison.OrdinalIgnoreCase) ? _preferredM1x :
        camera.StartsWith("M2", StringComparison.OrdinalIgnoreCase) ? _preferredM2x : null;
}
