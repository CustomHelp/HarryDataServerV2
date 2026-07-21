using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>Raised when a Part Exit telegram (channel 2) has been received and parsed.</summary>
public sealed class SpsPartExitEventArgs : EventArgs
{
    public SpsPartExitEventArgs(SpsPartExitData data) => Data = data;
    public SpsPartExitData Data { get; }
}

/// <summary>
/// TCP server for the 7 PLC/SPS channels (CLAUDE.md section 5). We are always the
/// server. Channel 1 mirrors + appends camera status, channel 2 raises Part Exit
/// events, channels 3–7 answer MSA trigger requests via <see cref="MsaRequestHandler"/>.
/// </summary>
public interface ISpsServer
{
    bool IsRunning { get; }

    /// <summary>Number of channels currently bound and listening.</summary>
    int ListeningChannels { get; }

    /// <summary>Number of currently connected PLC clients across all channels.</summary>
    int ActiveConnections { get; }

    /// <summary>Raised on listener/connection changes (for UI binding).</summary>
    event Action? StatusChanged;

    /// <summary>Raised when a Part Exit telegram is received (drives CSV/Collage/MSA later).</summary>
    event EventHandler<SpsPartExitEventArgs>? PartExitReceived;

    /// <summary>
    /// Raised for every received request and every sent response on any channel
    /// (channel, isResponse, text). Used by the UI for per-channel telemetry.
    /// </summary>
    event Action<SpsChannel, bool, string>? ChannelActivity;

    /// <summary>Number of PLC clients currently connected on a specific channel.</summary>
    int ConnectionsOn(SpsChannel channel);

    /// <summary>
    /// Handler for MSA trigger requests (channels 3–7): given the module key
    /// ("M10".."M50") and the BaseID, returns the IMMEDIATE response word — always "Wait" in the
    /// push model (the completed OK/NG is delivered later via <see cref="PushMsaResultAsync"/> on
    /// the same open connection, without the PLC re-requesting). "Error;&lt;desc&gt;" only on a bad
    /// request format. Set by the MSA engine in Phase 10; until then a null handler answers "Wait".
    /// </summary>
    Func<string, string, string>? MsaRequestHandler { get; set; }

    /// <summary>
    /// Push a completed MSA result to the PLC on the module's channel WITHOUT the PLC polling again
    /// (push model, CLAUDE.md §5). <paramref name="status"/> is "OK"/"NG"/"Error;&lt;desc&gt;"; the
    /// BaseID is inserted as field 1 → "&lt;status&gt;;&lt;BaseID&gt;[;&lt;desc&gt;]", terminated with CR, on
    /// the SAME connection that sent the "Wait". Returns false (logged) when that channel has no open
    /// connection or the write fails. The PLC must keep the request connection open to receive it.
    /// </summary>
    Task<bool> PushMsaResultAsync(string moduleKey, string baseId, string status, CancellationToken ct = default);

    /// <summary>
    /// Async handler for Part Exit (channel 2). When set, the server defers the
    /// response: it runs the handler (orchestrated CSV/Collage/Images), then sends the
    /// ACK <c>serial.PadRight(32,'0') + ";" + true|false + "\r"</c> (single CR, uniform
    /// across all SPS channels; V1 used CR+LF). When null,
    /// the server falls back to an immediate "OK". Set by the part-exit orchestrator.
    /// </summary>
    Func<SpsPartExitData, Task<bool>>? PartExitHandler { get; set; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
