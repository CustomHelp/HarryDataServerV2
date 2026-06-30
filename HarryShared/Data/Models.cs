namespace HarryShared.Data;

/// <summary>An active measurement definition joined with its camera (display row).</summary>
public sealed record MeasurementDefinitionRow(
    int Id,
    int CameraId,
    string CameraName,
    string Module,
    int TelegramPlace,
    string VariableName,
    string DisplayName,
    string VarType,
    int ParameterSet,
    string FeatureGroup)
{
    /// <summary>M20/M21 measurements live in measurements_serial_trimmer; everything else in measurements_serial.</summary>
    public bool IsTrimmer => QueryService.IsTrimmerModule(Module);

    /// <summary>Label shown in pick lists: "M50_ST110_KF1 · Anode_Flatness_L".</summary>
    public string Label => $"{CameraName} · {DisplayName}";
}

/// <summary>One finished-part header row from <c>dmcserial</c>.</summary>
public sealed record PartInfo(
    int Id,
    string SerialNumber,
    string? SerialTrimmer,
    string? Dmc,
    int? M1xModule,
    int? M1xNest,
    int? M2xModule,
    int? M2xNest,
    string? M3xModule,
    string? M3xNest,
    string? M50Nest,
    string? OrderName,
    double? M1xTemperature,
    double? M1xHumidity,
    int ResultStatus,
    DateTime CreatedAt)
{
    /// <summary>
    /// True when no <c>dmcserial</c> part record exists yet and the part was synthesized from
    /// <c>measurements_serial(_trimmer)</c> by serial (camera data before the PLC part-exit). Such a
    /// part has no overall result, so <see cref="ResultText"/> shows a neutral marker.
    /// </summary>
    public bool Synthetic { get; init; }

    public string ResultText => Synthetic
        ? "— (no part record)"
        : ResultStatus switch
        {
            1 => "OK",
            0 => "NG",
            -1 => "Deleted",
            _ => ResultStatus.ToString(),
        };
}

/// <summary>One measurement of a part joined with its definition + limits (for HarryAnalysis).</summary>
public sealed record PartMeasurementRow(
    string DisplayName,
    string CameraName,
    string Module,
    string FeatureGroup,
    int ParameterSet,
    double? Value,
    string? ValueString,
    int? ResultStatus,
    double? Min,
    double? Max,
    DateTime MeasuredAt)
{
    public string ResultText => ResultStatus switch
    {
        1 => "OK",
        0 => "NG",
        -1 => "PosAdjErr",
        -2 => "NotValidated",
        2 => "NotEvaluated",
        null => string.Empty,
        _ => ResultStatus.Value.ToString(),
    };

    /// <summary>True when the result code is a "good" measurement (1).</summary>
    public bool IsOk => ResultStatus == 1;

    public string ValueText => Value?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                               ?? ValueString ?? string.Empty;

    public string MinText => Min?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    public string MaxText => Max?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>One time-series sample for HarryGraph.</summary>
public sealed record SeriesPoint(DateTime MeasuredAt, double Value, int? ResultStatus, string SerialNumber);

/// <summary>One grouped NG count for HarryCounter.</summary>
public sealed record CountRow(string GroupKey, int Count);

/// <summary>
/// One aggregated measurement-result row for the HarryCounter error tree: a distinct
/// combination of grouping dimensions with its OK (1) / NG (0) count.
/// </summary>
public sealed record ErrorAggRow(
    string FeatureGroup,
    string Measurement,
    string? M1xNest,
    string? M3xNest,
    string? M50Nest,
    int ResultStatus,
    int Count);

/// <summary>
/// Time-varying limit history for a (camera, parameter_set), used by HarryGraph to draw a
/// per-point Min/Max envelope (the limit at a measurement's timestamp = the latest setting
/// recorded at or before it).
/// </summary>
public sealed class LimitHistory
{
    private readonly List<(DateTime At, double Value)> _min;
    private readonly List<(DateTime At, double Value)> _max;

    public LimitHistory(List<(DateTime At, double Value)> min, List<(DateTime At, double Value)> max)
    {
        _min = min;
        _max = max;
    }

    public bool HasAny => _min.Count > 0 || _max.Count > 0;

    public double? MinAt(DateTime t) => ValueAt(_min, t);
    public double? MaxAt(DateTime t) => ValueAt(_max, t);

    /// <summary>The most recent value with At &lt;= t (step function); null if none precedes t.</summary>
    private static double? ValueAt(List<(DateTime At, double Value)> series, DateTime t)
    {
        if (series.Count == 0)
            return null;

        // Series is ascending by At — walk back to the latest entry at or before t.
        double? result = null;
        foreach (var (at, value) in series)
        {
            if (at <= t)
                result = value;
            else
                break;
        }
        // If every entry is after t, fall back to the earliest known limit so the line still draws.
        return result ?? series[0].Value;
    }
}
