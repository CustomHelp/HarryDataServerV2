using System.IO;
using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.Infrastructure;

/// <summary>
/// Resolves the per-run MSA/LimitSample output folder. Everything produced for one run is gathered
/// under <c>&lt;[MSA] ReportPath&gt;\yyyy-MM-dd\&lt;Module&gt;\&lt;BaseID&gt;\</c> with three subfolders:
/// <code>
///   PDF\  — the PDF reports (per part for MSA1/LimitSample, per run for MSA3)
///   RAW\  — the Minitab raw-data export
///   IMG\  — the run images, MOVED out of the GoldenSample transit folder
/// </code>
/// The date is the run's calendar day from the BaseID timestamp (not the current time), so it stays
/// stable even when the completion request arrives later than the run started. Both the MSA engine and
/// the PDF service resolve the same folder through this helper.
///
/// <para><b>Removed 2026-07-28:</b> the old <c>&lt;[MSA] ResultPath&gt;\YYYY\MM\DD\&lt;BaseID&gt;\{PDF,CSV,IMG}</c>
/// layout (<c>RunRoot</c>/<c>PdfDir</c>/<c>CsvDir</c>/<c>ImgDir</c>) and the MSA summary-CSV helpers.
/// <c>[MSA] ResultPath</c> was never set in the live config, so that code path was dead (finding B6),
/// and the summary CSV itself is gone — <c>ReportPath</c> is the single file-based MSA location.</para>
/// </summary>
public static class MsaResultLayout
{
    public const string PdfSubfolder = "PDF";
    public const string ImgSubfolder = "IMG";
    public const string RawSubfolder = "RAW";

    /// <summary>Built-in local fallback root for the report output when nothing else is set.</summary>
    public const string DefaultReportFallback = @"D:\HarryDataServer\MSA_Reports";

    /// <summary>
    /// The per-run report root <c>&lt;root&gt;\&lt;yyyy-MM-dd&gt;\&lt;Module&gt;\&lt;BaseID&gt;</c>
    /// (2026-07-21 layout). The three outputs live in its subfolders: <c>PDF\</c>, <c>RAW\</c>,
    /// <c>IMG\</c>. Date is the run's calendar day.
    /// </summary>
    public static string ReportRunRoot(string root, string module, string baseId, DateTime runAt) =>
        Path.Combine(root, runAt.ToString("yyyy-MM-dd"), Sanitize(module), Sanitize(baseId));

    /// <summary>
    /// Resolve AND create a writable per-run report root (task B/D). Tries the primary root
    /// (<c>[MSA] ReportPath</c>); if that cannot be created/written (network drive down, UNC
    /// unreachable, permission denied) it logs a WARNING and falls back to the local
    /// <c>[MSA] ReportFallbackPath</c> (or <see cref="DefaultReportFallback"/>) — SAME layout. The run
    /// is never lost and the caller never sees an exception from an unreachable network path.
    /// Returns the created run root (append PDF/RAW/IMG), or null only if even the local fallback fails.
    /// </summary>
    public static string? EnsureWritableReportDir(
        string reportPath, string reportFallbackPath, string resultPath, string module, string baseId, DateTime runAt, ILogService log)
    {
        var fallbackRoot =
            !string.IsNullOrWhiteSpace(reportFallbackPath) ? reportFallbackPath : DefaultReportFallback;

        // Primary preference: ReportPath → else ResultPath → else the local fallback.
        var primaryRoot =
            !string.IsNullOrWhiteSpace(reportPath) ? reportPath :
            !string.IsNullOrWhiteSpace(resultPath) ? resultPath :
            fallbackRoot;

        var primaryDir = ReportRunRoot(primaryRoot, module, baseId, runAt);
        try
        {
            Directory.CreateDirectory(primaryDir);
            return primaryDir;
        }
        catch (Exception ex)
        {
            var fallbackDir = ReportRunRoot(fallbackRoot, module, baseId, runAt);
            if (string.Equals(Path.GetFullPath(primaryDir), Path.GetFullPath(fallbackDir), StringComparison.OrdinalIgnoreCase))
            {
                log.Error(ex, "MSA report directory {Dir} could not be created (no fallback available).", primaryDir);
                return null;
            }

            log.Warning("MSA report path {Primary} not writable ({Message}); falling back to local {Fallback}.",
                primaryDir, ex.Message, fallbackDir);
            try
            {
                Directory.CreateDirectory(fallbackDir);
                return fallbackDir;
            }
            catch (Exception ex2)
            {
                log.Error(ex2, "MSA report fallback directory {Dir} could not be created either.", fallbackDir);
                return null;
            }
        }
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "UnknownBaseID";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
