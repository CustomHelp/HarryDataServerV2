using HarryDataServer.Communication;
using HarryDataServer.Configuration;
using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// Builds and supervises a <see cref="TcpCameraClient"/> for every camera defined
/// in Harry.ini (count is dynamic). Each client gets its parsed JSON templates and
/// shares the singleton <see cref="TelegramParser"/>.
/// </summary>
public sealed class CameraConnectionService : ICameraService
{
    private readonly ILogService _log;
    private readonly List<CameraConfig> _cameras;
    private readonly List<TcpCameraClient> _clients = new();

    public CameraConnectionService(
        IConfigService config,
        JsonTemplateLoader templateLoader,
        TelegramParser parser,
        ILogService log)
    {
        _log = log;
        _cameras = config.Config.Cameras.ToList();

        var templates = templateLoader.LoadAll(_cameras);

        foreach (var cam in _cameras)
        {
            if (!templates.TryGetValue(cam.CameraName, out var camTemplates))
                camTemplates = new CameraTemplates { CameraName = cam.CameraName };

            var client = new TcpCameraClient(cam, camTemplates, parser, log)
            {
                // Keyence version-variable request (confirmed from production V1).
                KeepAliveCommand = "MR,#Version\r",
            };
            client.StateChanged += (_, _) => StatusChanged?.Invoke();
            _clients.Add(client);
        }
    }

    public IReadOnlyList<TcpCameraClient> Clients => _clients;

    public int TotalCount => _clients.Count;

    public int ConnectedCount => _clients.Count(c => c.IsConnected);

    public event Action? StatusChanged;

    public async Task StartAsync(CancellationToken ct)
    {
        var started = 0;
        for (var i = 0; i < _clients.Count; i++)
        {
            if (!_cameras[i].AutoConnect)
            {
                _log.Information("{Camera}: AutoConnect disabled; not starting.", _cameras[i].CameraName);
                continue;
            }

            await _clients[i].StartAsync(ct).ConfigureAwait(false);
            started++;
        }

        _log.Information("Camera service started {Started}/{Total} client(s).", started, _clients.Count);
    }

    public async Task StopAsync()
    {
        await Task.WhenAll(_clients.Select(c => c.StopAsync())).ConfigureAwait(false);
        _log.Information("Camera service stopped.");
    }
}
