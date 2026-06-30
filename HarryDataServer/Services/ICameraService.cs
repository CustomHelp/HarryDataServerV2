using HarryDataServer.Communication;

namespace HarryDataServer.Services;

/// <summary>
/// Owns one <see cref="TcpCameraClient"/> per configured camera and manages their
/// lifecycle. Other subsystems (the measurement pipeline in Phase 4, the UI)
/// subscribe to the individual clients exposed via <see cref="Clients"/>.
/// </summary>
public interface ICameraService
{
    IReadOnlyList<TcpCameraClient> Clients { get; }

    int TotalCount { get; }
    int ConnectedCount { get; }

    /// <summary>Raised whenever any client's connection state changes (for UI binding).</summary>
    event Action? StatusChanged;

    /// <summary>Start all auto-connect camera clients.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stop all camera clients and wait for their loops to finish.</summary>
    Task StopAsync();

    /// <summary>
    /// Ask every <b>connected</b> camera to emit its Settings telegram (disconnected ones are
    /// skipped). Returns the number of cameras the command was sent to and the total camera count.
    /// </summary>
    Task<(int Sent, int Total)> RequestSettingsAllAsync();
}
