using System.Globalization;

namespace HarryDataServer.Models;

/// <summary>Final result of a part as reported at Part Exit.</summary>
public enum PartResult
{
    Unknown,
    Ok,
    Ng,
    Deleted,
}

/// <summary>
/// Parsed Part Exit telegram from SPS channel 2 (St160 packaging, CLAUDE.md section 5).
/// Semicolon-separated fields in the documented order. Maps to one <c>dmcserial</c> row.
/// </summary>
public sealed class SpsPartExitData
{
    public string Dmc { get; init; } = string.Empty;

    /// <summary>Frame serial number (SZID) → dmcserial.serial_number.</summary>
    public string Szid { get; init; } = string.Empty;

    /// <summary>Trimmer serial number → dmcserial.serial_trimmer.</summary>
    public string VirtualSerial { get; init; } = string.Empty;

    public string OrderName { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;

    public int? M1xModule { get; init; }
    public int? M1xNest { get; init; }
    public int? M2xModule { get; init; }
    public int? M2xNest { get; init; }
    public string M3xModule { get; init; } = string.Empty;
    public string M3xNest { get; init; } = string.Empty;
    public string M50Nest { get; init; } = string.Empty;

    /// <summary>Temperature from M1x → dmcserial.m1x_temperature.</summary>
    public double? Temperature { get; init; }
    public double? Humidity { get; init; }

    public PartResult Result { get; init; }
    public string RawTelegram { get; init; } = string.Empty;

    public bool IsMsa => Mode is "MSA1" or "MSA3" or "LimitSample";

    /// <summary>Result mapped to dmcserial.result_status (1=OK, 0=NG, -1=deleted).</summary>
    public int ResultStatusCode => Result switch
    {
        PartResult.Ok => 1,
        PartResult.Ng => 0,
        PartResult.Deleted => -1,
        _ => 0,
    };

    private const int FieldCount = 15;

    /// <summary>
    /// Parse a semicolon-separated Part Exit telegram. Returns null if it does not
    /// have at least the expected number of fields.
    /// </summary>
    public static SpsPartExitData? TryParse(string telegram)
    {
        if (string.IsNullOrWhiteSpace(telegram))
            return null;

        var f = telegram.Split(';');
        if (f.Length < FieldCount)
            return null;

        return new SpsPartExitData
        {
            Dmc = f[0].Trim(),
            Szid = f[1].Trim(),
            VirtualSerial = f[2].Trim(),
            OrderName = f[3].Trim(),
            Mode = f[4].Trim(),
            M1xModule = ParseInt(f[5]),
            M1xNest = ParseInt(f[6]),
            M2xModule = ParseInt(f[7]),
            M2xNest = ParseInt(f[8]),
            M3xModule = f[9].Trim(),
            M3xNest = f[10].Trim(),
            M50Nest = f[11].Trim(),
            Temperature = ParseDouble(f[12]),
            Humidity = ParseDouble(f[13]),
            Result = ParseResult(f[14]),
            RawTelegram = telegram,
        };
    }

    private static int? ParseInt(string raw) =>
        int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? ParseDouble(string raw) =>
        double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static PartResult ParseResult(string raw) => raw.Trim().ToUpperInvariant() switch
    {
        "OK" => PartResult.Ok,
        "NG" => PartResult.Ng,
        "DE" => PartResult.Deleted,
        _ => PartResult.Unknown,
    };
}
