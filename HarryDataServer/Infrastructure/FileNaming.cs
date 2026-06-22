using System.Globalization;

namespace HarryDataServer.Infrastructure;

/// <summary>
/// Central filename / folder naming conventions required by the SOW.
///
/// • §5.1.2 — every datetime token embedded in a generated filename uses the
///   <c>DDMMYY_HHMMSS</c> pattern (e.g. <c>220626_143022</c>).
/// • §1.2.1 — Golden Sample (GSM) output uses fixed root folder names and a
///   per-run subfolder <c>&lt;TestType&gt;_&lt;DDMMYY_HHMMSS&gt;_&lt;Module&gt;</c>.
///
/// Keeping these in one place means no magic date strings are scattered across
/// the CSV / MSA / image / report writers.
/// </summary>
public static class FileNaming
{
    /// <summary>SOW §5.1.2 datetime token format for filenames: DDMMYY_HHMMSS.</summary>
    public const string DateTimePattern = "ddMMyy_HHmmss";

    /// <summary>Format a timestamp as the SOW filename datetime token (DDMMYY_HHMMSS).</summary>
    public static string Stamp(DateTime when) =>
        when.ToString(DateTimePattern, CultureInfo.InvariantCulture);

    // --- Golden Sample (GSM) folder names (SOW §1.2.1, exact spelling) ---

    /// <summary>Root folder for Golden Sample CSV data.</summary>
    public const string GoldenSampleDataFolder = "Golden Sample Data";

    /// <summary>Root folder for Golden Sample images.</summary>
    public const string GoldenSampleImagesFolder = "Golden Sample Images";

    /// <summary>
    /// Per-run subfolder name shared by GSM CSV and image output:
    /// <c>&lt;TestType&gt;_&lt;DDMMYY_HHMMSS&gt;_&lt;Module&gt;</c> (e.g. <c>MSA1_220626_143022_M50</c>).
    /// </summary>
    public static string GoldenSampleRunFolder(string testType, DateTime when, string module) =>
        $"{testType}_{Stamp(when)}_{module}";
}
