using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// MSA engine (CLAUDE.md section 7). Stores MSA-mode measurements into
/// <c>msa_measurements</c> and, when the PLC requests an evaluation (SPS channels
/// 3–7), computes Cg/Cgk (MSA1), %Tolerance (MSA3) or LimitSample pass/fail,
/// writes <c>msa_results</c> + an MSA CSV, and answers Wait/OK/NG.
/// </summary>
public interface IMsaService
{
    int PendingCount { get; }

    /// <summary>
    /// Load the stored MSA runs for a module ("M10".."M50") and type, oldest first,
    /// from <c>msa_results</c> (grouped by BaseID). Empty if the DB is not ready.
    /// </summary>
    Task<IReadOnlyList<MsaRunDto>> GetRunsAsync(string module, MsaType type, CancellationToken ct = default);

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
