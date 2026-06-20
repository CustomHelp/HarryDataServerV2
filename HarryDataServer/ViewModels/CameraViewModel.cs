using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryDataServer.Communication;
using HarryDataServer.Models;

namespace HarryDataServer.ViewModels;

/// <summary>
/// View model for one camera client (one ucCameraControl per instance). Read-only
/// mirror of <see cref="TcpCameraClient"/>; scalar fields are refreshed on the UI
/// thread by the MainViewModel timer. The last 3 telegrams are kept in a small
/// queue fed by the camera's background event (no DB access).
/// </summary>
public sealed partial class CameraViewModel : ObservableObject
{
    private const int RecentTelegramCount = 3;

    private readonly TcpCameraClient _client;
    private readonly object _gate = new();
    private readonly Queue<string> _recent = new(RecentTelegramCount);
    private volatile bool _recentDirty;

    public CameraViewModel(TcpCameraClient client)
    {
        _client = client;
        Name = client.CameraName;
        Module = client.Module;
        Ip = client.Ip;
        Port = client.Port;
        Endpoint = client.Endpoint;
        ReconnectCommand = new RelayCommand(() => _client.RequestReconnect());

        client.TelegramReceived += OnTelegram;
        Update();
    }

    public string Name { get; }
    public string Module { get; }
    public string Ip { get; }
    public int Port { get; }
    public string Endpoint { get; }
    public IRelayCommand ReconnectCommand { get; }

    public ObservableCollection<string> RecentTelegrams { get; } = new();

    [ObservableProperty] private string _stateText = "—";
    [ObservableProperty] private long _telegramCount;
    [ObservableProperty] private Brush _connectedBrush = Brushes.Gray;
    [ObservableProperty] private Brush _jsonBrush = Brushes.Gray;
    [ObservableProperty] private Brush _reconnectBrush = Brushes.Gray;

    private void OnTelegram(object? sender, ParsedTelegram telegram)
    {
        // Background thread: only touch the thread-safe queue here.
        var line = $"{DateTime.Now:HH:mm:ss}  {telegram.RawSignal}/{telegram.Mode}  {Trim(telegram.Serial1)}";
        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > RecentTelegramCount)
                _recent.Dequeue();
        }
        _recentDirty = true;
    }

    /// <summary>Pull current state from the client. Called on the UI thread.</summary>
    public void Update()
    {
        var state = _client.State;
        StateText = state.ToString();
        TelegramCount = _client.TelegramCount;

        ConnectedBrush = state switch
        {
            CameraConnectionState.Connected => Led.Green,
            CameraConnectionState.Connecting => Led.Orange,
            _ => Led.Red,
        };
        JsonBrush = _client.JsonLoaded ? Led.Green : Led.Red;
        ReconnectBrush = _client.AutoReconnectActive ? Led.Green : Led.Gray;

        if (_recentDirty)
        {
            _recentDirty = false;
            string[] snapshot;
            lock (_gate)
                snapshot = _recent.ToArray();

            RecentTelegrams.Clear();
            // Newest on top.
            for (var i = snapshot.Length - 1; i >= 0; i--)
                RecentTelegrams.Add(snapshot[i]);
        }
    }

    private static string Trim(string s) => s.Length <= 20 ? s : s[..20] + "…";
}
