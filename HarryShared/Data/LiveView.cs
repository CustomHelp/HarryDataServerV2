namespace HarryShared.Data;

/// <summary>
/// Shared definitions for the companion tools' live view "last N parts" selector (an editable
/// combo with presets plus a free-typed value). Keeps HarryGraph and HarryCounter consistent.
/// </summary>
public static class LiveView
{
    /// <summary>Preset record counts offered in the dropdown.</summary>
    public static readonly IReadOnlyList<int> Presets = new[] { 10, 100, 1000, 10000 };

    /// <summary>Default when nothing valid is chosen.</summary>
    public const int DefaultCount = 100;

    /// <summary>Hard ceiling so a silly custom value can't pull the whole table.</summary>
    public const int MaxCount = 100_000;

    /// <summary>
    /// Parse an editable-combo entry to a valid part count (1..<see cref="MaxCount"/>), falling
    /// back to <paramref name="fallback"/> on empty / non-numeric / non-positive input.
    /// </summary>
    public static int ParseCount(string? text, int fallback)
    {
        if (int.TryParse(text?.Trim(), out var n) && n > 0)
            return Math.Min(n, MaxCount);
        return fallback;
    }
}
