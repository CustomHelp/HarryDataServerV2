using System.IO;
using HarryDataServer.Models;

namespace HarryDataServer.Infrastructure;

/// <summary>
/// Resolves the per-run MSA/LimitSample result collection folder (task: image naming /
/// MSA result folders). Everything produced for one run is gathered under
/// <c>&lt;ResultPath&gt;\YYYY\MM\DD\&lt;BaseID&gt;\</c> with three subfolders:
/// <code>
///   PDF\  — the 2 PDF reports (AllResults + FailuresOnly)
///   CSV\  — the MSA measurement CSV
///   IMG\  — all run images (moved out of the GoldenSample input folder)
/// </code>
/// The date is derived from the BaseID timestamp (not the current time), so it stays
/// stable even when the completion request arrives later than the run started. Both the
/// MSA engine and the PDF service resolve the same folder through this helper.
/// </summary>
public static class MsaResultLayout
{
    public const string PdfSubfolder = "PDF";
    public const string CsvSubfolder = "CSV";
    public const string ImgSubfolder = "IMG";

    /// <summary>
    /// The run root <c>&lt;root&gt;\YYYY\MM\DD\&lt;BaseID&gt;</c>. <paramref name="resultPath"/> is
    /// <c>[MSA] ResultPath</c>; when empty it falls back to <c>&lt;ReferencePath&gt;\MSA_Results</c>
    /// and finally the application directory, so a run is never lost.
    /// </summary>
    public static string RunRoot(string resultPath, string referencePath, string baseId)
    {
        var root =
            !string.IsNullOrWhiteSpace(resultPath) ? resultPath :
            !string.IsNullOrWhiteSpace(referencePath) ? Path.Combine(referencePath, "MSA_Results") :
            Path.Combine(AppContext.BaseDirectory, "MSA_Results");

        var date = BaseId.TryGetTimestamp(baseId, out var dt) ? dt : DateTime.Now;
        return Path.Combine(root, date.ToString("yyyy"), date.ToString("MM"), date.ToString("dd"), Sanitize(baseId));
    }

    public static string PdfDir(string resultPath, string referencePath, string baseId) =>
        Path.Combine(RunRoot(resultPath, referencePath, baseId), PdfSubfolder);

    public static string CsvDir(string resultPath, string referencePath, string baseId) =>
        Path.Combine(RunRoot(resultPath, referencePath, baseId), CsvSubfolder);

    public static string ImgDir(string resultPath, string referencePath, string baseId) =>
        Path.Combine(RunRoot(resultPath, referencePath, baseId), ImgSubfolder);

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "UnknownBaseID";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
