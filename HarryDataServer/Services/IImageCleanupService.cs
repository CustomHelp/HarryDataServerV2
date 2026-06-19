namespace HarryDataServer.Services;

/// <summary>
/// Periodic NAS image retention + DB partition retention (CLAUDE.md sections 8 + 11).
/// Deletes NG/Diagnostic/GoldenSample images older than their configured retention,
/// drops expired measurement partitions, and removes orphaned OK images when a part
/// leaves as NG. Runs as a lowest-priority background job.
/// </summary>
public interface IImageCleanupService
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
