using System.Globalization;

namespace HarryDataServer.Models;

/// <summary>
/// The 19-character BaseID that identifies an MSA run (CLAUDE.md section 6),
/// e.g. <c>5026061608560272010</c>.
/// </summary>
public sealed class BaseId
{
    public required string Raw { get; init; }
    public int Module { get; init; }
    public int Year { get; init; }
    public int Month { get; init; }
    public int Day { get; init; }
    public int Hour { get; init; }
    public int Minute { get; init; }
    public int Second { get; init; }
    public int TrayRow { get; init; }
    public int TrayCol { get; init; }
    public int Loop1 { get; init; }
    public int Loop2 { get; init; }
    public int Loop3 { get; init; }

    public static BaseId? TryParse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length != 19 || !raw.All(char.IsDigit))
            return null;

        int N(int start, int len) => int.Parse(raw.Substring(start, len), CultureInfo.InvariantCulture);

        return new BaseId
        {
            Raw = raw,
            Module = N(0, 2),
            Year = N(2, 2),
            Month = N(4, 2),
            Day = N(6, 2),
            Hour = N(8, 2),
            Minute = N(10, 2),
            Second = N(12, 2),
            TrayRow = N(14, 1),
            TrayCol = N(15, 1),
            Loop1 = N(16, 1),
            Loop2 = N(17, 1),
            Loop3 = N(18, 1),
        };
    }
}
