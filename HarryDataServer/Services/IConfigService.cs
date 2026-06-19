using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// Provides read access to the loaded Harry.ini configuration. The configuration
/// is loaded once at startup and held as an immutable snapshot.
/// </summary>
public interface IConfigService
{
    /// <summary>The fully parsed configuration.</summary>
    AppConfig Config { get; }

    /// <summary>Absolute path of the Harry.ini file that was loaded.</summary>
    string IniPath { get; }

    /// <summary>Reload the configuration from disk. Returns the refreshed snapshot.</summary>
    AppConfig Reload();
}
