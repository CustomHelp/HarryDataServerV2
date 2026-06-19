using HarryDataServer.Configuration;
using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// Singleton <see cref="IConfigService"/> implementation. Loads Harry.ini once
/// at construction and caches the resulting <see cref="AppConfig"/> snapshot.
/// </summary>
public sealed class IniConfigService : IConfigService
{
    private readonly IniConfigManager _manager = new();

    public IniConfigService(string iniPath)
    {
        IniPath = iniPath;
        Config = _manager.Load(iniPath);
    }

    public AppConfig Config { get; private set; }

    public string IniPath { get; }

    public AppConfig Reload()
    {
        Config = _manager.Load(IniPath);
        return Config;
    }
}
