using HarryShared.Data;

namespace HarryAnalysis;

/// <summary>
/// One scanned part kept in the in-memory history list (not persisted). Holds the part
/// header, all its measurements, and which field the scan matched on.
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
    }

    public DateTime Timestamp { get; }
    public string Scan { get; }
    public string MatchedField { get; }
    public PartInfo Part { get; }
    public IReadOnlyList<PartMeasurementRow> Measurements { get; }

    public string TimestampText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
    public string Dmc => Part.Dmc ?? "-";
    public string Szid => Part.SerialNumber;
    public int ResultStatus => Part.ResultStatus;
    public string ResultText => Part.ResultText;
}
