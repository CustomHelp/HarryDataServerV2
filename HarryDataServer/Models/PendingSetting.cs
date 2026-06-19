namespace HarryDataServer.Models;

/// <summary>
/// One Min/Max limit queued for insertion into the <c>settings</c> history table.
/// Built on the camera receive thread (no I/O); <c>camera_id</c>/<c>definition_id</c>
/// are resolved at flush time from the setting-definition cache.
/// </summary>
public sealed class PendingSetting
{
    public required string CameraName { get; init; }
    public required string SettingName { get; init; }
    public int ParameterSet { get; init; }
    public double LimitValue { get; init; }

    /// <summary>Camera program version from the telegram header (position 1).</summary>
    public string? Version { get; init; }

    public DateTime RecordedAt { get; init; }
}
