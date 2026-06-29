using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>The two PDF reports produced for one MSA run (SOW §3.2.1).</summary>
public sealed record MsaReportPaths(string AllResults, string FailuresOnly);

/// <summary>
/// Generates the MSA PDF reports required by SOW §3.2.1: one with every measurement
/// result and one with only the failed entries. Reports are written to the run's PDF
/// subfolder under <c>[MSA] ResultPath\YYYY\MM\DD\&lt;BaseID&gt;\PDF</c>.
/// </summary>
public interface IPdfReportService
{
    /// <summary>The deterministic output paths for a run, without generating anything.</summary>
    MsaReportPaths ResolvePaths(MsaReportData report);

    /// <summary>Generate both PDF reports for the run and return their paths.</summary>
    MsaReportPaths Generate(MsaReportData report);
}
