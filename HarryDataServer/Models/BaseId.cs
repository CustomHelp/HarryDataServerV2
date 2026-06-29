using System.Globalization;

namespace HarryDataServer.Models;

/// <summary>
/// The 14-character BaseID that identifies an MSA run (CLAUDE.md section 6),
/// format <c>MMYYMMDDHHmmSS</c>, e.g. <c>10260623083000</c> = M10, 2026-06-23, 08:30:00.
/// During a run each loop telegram appends a 3-digit loop counter (001, 002, …) to the
/// BaseID in the serial field; that counter is parsed out separately by
/// <see cref="TrySplitRun"/> and stored in <c>msa_measurements.loop_number</c>. The
/// BaseID itself (these 14 chars) stays constant across all stations and loops of a run.
/// </summary>
public sealed class BaseId
{
    /// <summary>Length of the BaseID itself (MMYYMMDDHHmmSS).</summary>
    public const int Length = 14;

    /// <summary>Length of the loop counter appended to the BaseID in run telegrams.</summary>
    public const int LoopLength = 3;

    public required string Raw { get; init; }
    public int Module { get; init; }
    public int Year { get; init; }
    public int Month { get; init; }
    public int Day { get; init; }
    public int Hour { get; init; }
    public int Minute { get; init; }
    public int Second { get; init; }

    /// <summary>Parse a bare 14-char BaseID (no loop counter), e.g. from the SPS completion request.</summary>
    public static BaseId? TryParse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim();
        if (s.Length != Length || !s.All(char.IsDigit))
            return null;

        int N(int start, int len) => int.Parse(s.Substring(start, len), CultureInfo.InvariantCulture);

        return new BaseId
        {
            Raw = s,
            Module = N(0, 2),
            Year = N(2, 2),
            Month = N(4, 2),
            Day = N(6, 2),
            Hour = N(8, 2),
            Minute = N(10, 2),
            Second = N(12, 2),
        };
    }

    /// <summary>
    /// Split a run serial field ("&lt;14-char BaseID&gt;&lt;3-digit loop&gt;", e.g. <c>10260623083000001</c>)
    /// into the 14-char <paramref name="baseId"/> and the integer <paramref name="loopNumber"/>.
    /// The BaseID returned never includes the loop counter. Returns false if the field does
    /// not start with 14 numeric BaseID characters.
    /// </summary>
    public static bool TrySplitRun(string serialField, out string baseId, out int loopNumber)
    {
        baseId = string.Empty;
        loopNumber = 0;

        if (string.IsNullOrWhiteSpace(serialField))
            return false;

        var s = serialField.Trim();
        if (s.Length < Length)
            return false;

        var head = s.Substring(0, Length);
        if (!head.All(char.IsDigit))
            return false;

        baseId = head;

        if (s.Length > Length)
        {
            var loopPart = s.Substring(Length);
            if (loopPart.Length > LoopLength)
                loopPart = loopPart.Substring(0, LoopLength);
            int.TryParse(loopPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out loopNumber);
        }

        return true;
    }

    /// <summary>The run timestamp encoded in the BaseID (2000 + the 2-digit year).</summary>
    public DateTime ToDateTime() => new(2000 + Year, Month, Day, Hour, Minute, Second);

    /// <summary>
    /// Derive the run date/time from a (bare 14-char) BaseID string. Used to place the MSA
    /// result folder under YYYY\MM\DD by the BaseID timestamp — not the current time
    /// (the completion request can arrive later than the run started).
    /// </summary>
    public static bool TryGetTimestamp(string baseId, out DateTime timestamp)
    {
        timestamp = default;
        if (TryParse(baseId) is not { } b)
            return false;
        try
        {
            timestamp = b.ToDateTime();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false; // impossible date components
        }
    }
}
