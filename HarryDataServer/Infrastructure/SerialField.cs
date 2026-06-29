namespace HarryDataServer.Infrastructure;

/// <summary>
/// Serial1 (SZID / Virtual Serial in Normal mode, BaseID+loop in MSA mode) is stored in at
/// most 22 characters (CLAUDE.md §4). Both the camera telegram (positions 4–35) and the SPS
/// part-exit telegram deliver it in a wider field whose trailing characters are padding; only
/// the first 22 are meaningful (SPS agreement). The DB serial columns are <c>VARCHAR(22)</c>
/// to match the 22-char Field 1 of the Keyence image filenames. This type is the single
/// definition of that width and the truncation guard used at the parser and the DB insert sites.
/// </summary>
public static class SerialField
{
    /// <summary>Maximum stored length of a Serial1 value (matches the VARCHAR(22) columns).</summary>
    public const int MaxLength = 22;

    /// <summary>Cap a serial value to <see cref="MaxLength"/> chars (no-op when already shorter).</summary>
    public static string Cap(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= MaxLength ? value ?? string.Empty : value[..MaxLength];
}
