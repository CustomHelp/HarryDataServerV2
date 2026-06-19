namespace HarryDataServer.Models;

/// <summary>
/// Configuration for a single Keyence camera controller, read from a
/// [CameraN] section in Harry.ini. The application is always the TCP client.
/// The number of cameras is dynamic — never hardcoded.
/// </summary>
public sealed class CameraConfig
{
    /// <summary>INI section index, e.g. 1 for [Camera1].</summary>
    public int Index { get; init; }

    /// <summary>Controller name, e.g. "M50_ST110_KF1". Matches telegram position 0.</summary>
    public string CameraName { get; init; } = string.Empty;

    /// <summary>Module the camera belongs to, derived from the camera name (e.g. "M50").</summary>
    public string Module { get; init; } = string.Empty;

    public string Ip { get; init; } = string.Empty;

    public int Port { get; init; }

    /// <summary>Absolute path to the Result_*.json template for this camera.</summary>
    public string JsonParameters { get; init; } = string.Empty;

    /// <summary>Absolute path to the Settings_*.json template for this camera.</summary>
    public string JsonSettings { get; init; } = string.Empty;

    /// <summary>Whether the client should connect automatically on startup.</summary>
    public bool AutoConnect { get; init; } = true;
}
