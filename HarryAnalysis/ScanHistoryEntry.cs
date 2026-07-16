using HarryShared.Data;

namespace HarryAnalysis;

/// <summary>
/// One scan kept in the in-memory history list (not persisted). Normally holds the part header, all
/// its measurements and the matched field. It can also represent a <b>miss</b> (no part found in the
/// database) via the second constructor — those rows carry no <see cref="Part"/> and render a red
/// "NICHT GEFUNDEN" badge against the scanned value, so failed lookups stay visible in the list and
/// not just in the status label.
/// </summary>
public sealed class ScanHistoryEntry
{
    public ScanHistoryEntry(DateTime timestamp, string scan, string matchedField,
        PartInfo part, IReadOnlyList<PartMeasurementRow> measurements)
    {
        Timestamp = timestamp;
        Scan = scan;
        MatchedField = matchedField;
        Part = part;
        Measurements = measurements;
        Found = true;
    }

    /// <summary>Create a "not found" entry for a scan that matched no part in the database.</summary>
    public ScanHistoryEntry(DateTime timestamp, string scan)
    {
        Timestamp = timestamp;
        Scan = scan;
        MatchedField = "—";
        Part = null;
        Measurements = Array.Empty<PartMeasurementRow>();
        Found = false;
    }

    /// <summary>False when the scan matched no part (miss row).</summary>
    public bool Found { get; }

    public DateTime Timestamp { get; }
    public string Scan { get; }
    public string MatchedField { get; }
    public PartInfo? Part { get; }
    public IReadOnlyList<PartMeasurementRow> Measurements { get; }

    public string TimestampText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
    public string Dmc => Found ? (Part!.Dmc ?? "-") : "-";
    public string Szid => Found ? Part!.SerialNumber : Scan;

    // Miss rows return 0 so the shared ResultStatusToBrush renders a red badge (paired with the
    // "NICHT GEFUNDEN" text below); real parts return their own OK/NG/deleted status.
    public int ResultStatus => Found ? Part!.ResultStatus : 0;
    public string ResultText => Found ? Part!.ResultText : "NICHT GEFUNDEN";
}
