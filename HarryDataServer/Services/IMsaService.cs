using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// MSA engine (CLAUDE.md section 7). Stores MSA-mode measurements into
/// <c>msa_measurements</c> and, when the PLC requests an evaluation (SPS channels
/// 3–7), computes Cg/Cgk (MSA1), %Tolerance (MSA3) or LimitSample pass/fail,
/// writes <c>msa_results</c> + an MSA CSV, and answers Wait/OK/NG.
/// </summary>
/// <summary>Fired after one run's evaluation has been stored (task A2): the MSA UI refreshes.</summary>
public sealed record MsaRunCompleted(string Module, MsaType Type, string BaseId);

public interface IMsaService
{
    int PendingCount { get; }

    /// <summary>Raised (on a background thread) once a run's <c>msa_results</c> are written, so the MSA
    /// tab can refresh its history and jump to / offer the new run (task A2).</summary>
    event Action<MsaRunCompleted>? RunCompleted;

    /// <summary>
    /// Load the stored MSA runs for a module ("M10".."M50") and type, oldest first,
    /// from <c>msa_results</c> (grouped by BaseID). Empty if the DB is not ready.
    /// </summary>
    Task<IReadOnlyList<MsaRunDto>> GetRunsAsync(string module, MsaType type, CancellationToken ct = default);

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
