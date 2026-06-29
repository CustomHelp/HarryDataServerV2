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

    // Last received Results-telegram state (guarded by _gate; applied to the bound
    // properties on the UI thread in Update()).
    private CameraOperatingMode _lastMode = CameraOperatingMode.Unknown;
    private bool _lastDiagnostic;
    private bool _hasModeInfo;

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

    /// <summary>Operating mode of the last received Results telegram (display text).</summary>
    [ObservableProperty] private string _modeText = "—";

    /// <summary>Mode_Diagnostic flag of the last received Results telegram (independent of <see cref="ModeText"/>).</summary>
    [ObservableProperty] private bool _isDiagnostic;

    private void OnTelegram(object? sender, ParsedTelegram telegram)
    {
        // Background thread: only touch the thread-safe queue / guarded fields here.
        var line = $"{DateTime.Now:HH:mm:ss}  {Describe(telegram)}  {Trim(telegram.Serial1)}";
        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > RecentTelegramCount)
                _recent.Dequeue();

            // Only Results telegrams carry the mode/diagnostic block.
            if (telegram.Signal == TelegramSignal.Results)
            {
                _lastMode = telegram.Mode;
                _lastDiagnostic = telegram.IsDiagnostic;
                _hasModeInfo = true;
            }
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

        bool hasMode;
        CameraOperatingMode lastMode;
        bool lastDiag;
        string[]? snapshot = null;
        lock (_gate)
        {
            hasMode = _hasModeInfo;
            lastMode = _lastMode;
            lastDiag = _lastDiagnostic;
            if (_recentDirty)
            {
                _recentDirty = false;
                snapshot = _recent.ToArray();
            }
        }

        if (hasMode)
        {
            ModeText = ModeToText(lastMode);
            IsDiagnostic = lastDiag;
        }

        if (snapshot is not null)
        {
            RecentTelegrams.Clear();
            // Newest on top.
            for (var i = snapshot.Length - 1; i >= 0; i--)
                RecentTelegrams.Add(snapshot[i]);
        }
    }

    /// <summary>Operating-mode display text (CLAUDE.md §4): LimitSample shows "Limit".</summary>
    private static string ModeToText(CameraOperatingMode mode) => mode switch
    {
        CameraOperatingMode.Normal => "Normal Operation",
        CameraOperatingMode.Msa1 => "MSA1",
        CameraOperatingMode.Msa3 => "MSA3",
        CameraOperatingMode.LimitSample => "Limit",
        _ => "—",
    };

    /// <summary>One-line description of a telegram for the recent-telegram list.</summary>
    private static string Describe(ParsedTelegram t) =>
        t.Signal == TelegramSignal.Results
            ? $"{ModeToText(t.Mode)}{(t.IsDiagnostic ? " ·Diag" : string.Empty)}  {ResultToText(t.OverallResult)}"
            : t.RawSignal;

    /// <summary>Total_Result code → short display token (display only; not a routing decision).</summary>
    private static string ResultToText(int? result) => result switch
    {
        1 => "IO",
        0 => "NG",
        -1 => "PosErr",
        -2 => "NotVal",
        2 => "Off",
        null => string.Empty,
        _ => result.Value.ToString(),
    };

    private static string Trim(string s) => s.Length <= 20 ? s : s[..20] + "…";
}
