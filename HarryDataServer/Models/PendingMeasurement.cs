namespace HarryDataServer.Models;

/// <summary>
/// One measurement queued for insertion into <c>measurements_serial</c> or
/// <c>measurements_serial_trimmer</c>. Built on the camera receive thread (no I/O)
/// and drained by the <see cref="HarryDataServer.Services.MeasurementProcessor"/>.
/// <c>definition_id</c> is resolved at flush time from the definition cache.
/// </summary>
public sealed class PendingMeasurement
{
    public required string CameraName { get; init; }
    public required string VariableName { get; init; }

    /// <summary>Serial value: SZID for production cameras, virtual serial for M20/M21.</summary>
    public required string Serial { get; init; }

    /// <summary>True → write to <c>measurements_serial_trimmer</c> (M20/M21).</summary>
    public bool IsTrimmer { get; init; }

    public double? Value { get; init; }
    public string? MeasurementString { get; init; }
    public int? ResultStatus { get; init; }

    /// <summary>0=Normal,1=MSA1,2=MSA3,3=LimitSample,4=GoldenSample.</summary>
    public byte RunType { get; init; }

    public DateTime MeasuredAt { get; init; }
}
