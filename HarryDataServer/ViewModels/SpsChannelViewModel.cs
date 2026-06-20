using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>
/// View model for one SPS channel (one ucSpsChannelControl per instance). Records the
/// last 2 received requests and last 2 sent responses (fed by the server's background
/// activity event) and the live connection state / message counter.
/// </summary>
public sealed partial class SpsChannelViewModel : ObservableObject
{
    private const int Keep = 2;

    private readonly ISpsServer _sps;
    private readonly SpsChannel _channel;
    private readonly object _gate = new();
    private readonly Queue<string> _requests = new(Keep);
    private readonly Queue<string> _responses = new(Keep);
    private volatile bool _dirty;
    private long _messageCount;

    public SpsChannelViewModel(ISpsServer sps, SpsChannel channel, int port)
    {
        _sps = sps;
        _channel = channel;
        Number = channel.Number();
        Description = channel.Description();
        Port = port;
        Update();
    }

    public int Number { get; }
    public string Description { get; }
    public int Port { get; }
    public string Title => $"Ch {Number} — {Description}";

    public ObservableCollection<string> LastRequests { get; } = new();
    public ObservableCollection<string> LastResponses { get; } = new();

    [ObservableProperty] private long _messages;
    [ObservableProperty] private Brush _connectedBrush = Brushes.Gray;

    /// <summary>Called from the server's background activity event.</summary>
    public void Record(bool isResponse, string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {Shorten(text)}";
        lock (_gate)
        {
            var q = isResponse ? _responses : _requests;
            q.Enqueue(line);
            while (q.Count > Keep)
                q.Dequeue();
        }
        if (!isResponse)
            Interlocked.Increment(ref _messageCount);
        _dirty = true;
    }

    /// <summary>Refresh on the UI thread.</summary>
    public void Update()
    {
        Messages = Interlocked.Read(ref _messageCount);
        ConnectedBrush = _sps.ConnectionsOn(_channel) > 0 ? Led.Green : Led.Gray;

        if (!_dirty)
            return;
        _dirty = false;

        Sync(LastRequests, _requests);
        Sync(LastResponses, _responses);
    }

    private void Sync(ObservableCollection<string> target, Queue<string> source)
    {
        string[] snapshot;
        lock (_gate)
            snapshot = source.ToArray();

        target.Clear();
        for (var i = snapshot.Length - 1; i >= 0; i--) // newest on top
            target.Add(snapshot[i]);
    }

    private static string Shorten(string s) => s.Length <= 48 ? s : s[..48] + "…";
}
