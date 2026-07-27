namespace HarryDataServer.Services;

/// <summary>
/// The single, central retention job (CLAUDE.md §11a, [Retention] section). Runs at startup and then
/// every 24 h and ages out EVERYTHING by its configured number of days (0 = never):
/// images (NG/Diagnostic/GoldenSample/Collage/Backup day-folders + \Input leftovers), MSA reports,
/// CSV exports, and the database (DROP PARTITION on the partitioned production tables, batch DELETE on
/// dmcserial and the MSA tables). Master data (settings, definitions, cameras) is NEVER touched.
/// Replaces the former ImageCleanupService (which only covered images + partitions).
/// </summary>
public interface IRetentionService
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
