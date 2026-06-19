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

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
