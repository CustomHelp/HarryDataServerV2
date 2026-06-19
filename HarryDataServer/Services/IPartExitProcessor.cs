namespace HarryDataServer.Services;

/// <summary>
/// Consumes Part Exit telegrams from the SPS server (channel 2) and upserts the
/// finished-part row into <c>dmcserial</c>. Same background-queue pattern as
/// <see cref="IMeasurementProcessor"/>. (CSV/Collage/MSA triggers are added in
/// later phases.)
/// </summary>
public interface IPartExitProcessor
{
    int PendingCount { get; }
    long TotalUpserted { get; }
    event Action? StatsChanged;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
