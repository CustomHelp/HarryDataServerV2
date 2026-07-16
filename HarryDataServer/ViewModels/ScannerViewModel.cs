using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>One row in the Scanner tab's grid: formatted timestamp + the raw DMC code.</summary>
public sealed record ScanRow(string Timestamp, string Code);

/// <summary>
/// View model for the Scanner tab. Mirrors the <see cref="IScannerService"/> ring buffer into a
/// bound collection (newest on top, capped at the service's <see cref="IScannerService.MaxRows"/>),
/// and reflects the scanner + companion connection status as LEDs. Service events fire on background
/// threads and are marshalled onto the dispatcher before touching the bound collection.
/// </summary>
public sealed partial class ScannerViewModel : ObservableObject
{
    private readonly IScannerService _scanner;

    public ScannerViewModel(IScannerService scanner)
    {
        _scanner = scanner;

        // Prime from any scans already buffered before the tab was built (oldest → insert reverses to newest-top).
        foreach (var entry in _scanner.RecentScans())
            Scans.Insert(0, ToRow(entry));

        _scanner.ScanReceived += OnScanReceived;
        _scanner.StatusChanged += OnStatusChanged;
        UpdateStatus();
    }

    /// <summary>Received scans, newest first (in-memory only, cleared on server restart).</summary>
    public ObservableCollection<ScanRow> Scans { get; } = new();

    [ObservableProperty] private string _scannerStatus = "Waiting for scanner…";
    [ObservableProperty] private Brush _scannerLed = Led.Gray;
    [ObservableProperty] private string _companionStatus = "0 companion apps connected";
    [ObservableProperty] private Brush _companionLed = Led.Gray;

    private void OnScanReceived(ScanEntry entry)
    {
        Post(() =>
        {
            Scans.Insert(0, ToRow(entry));
            while (Scans.Count > _scanner.MaxRows)
                Scans.RemoveAt(Scans.Count - 1);
        });
    }

    private void OnStatusChanged() => Post(UpdateStatus);

    private void UpdateStatus()
    {
        if (!_scanner.IsListening)
        {
            ScannerStatus = "Scanner listener not started";
            ScannerLed = Led.Gray;
        }
        else if (_scanner.ScannerConnected)
        {
            ScannerStatus = "Scanner connected";
            ScannerLed = Led.Green;
        }
        else
        {
            ScannerStatus = "Waiting for scanner…";
            ScannerLed = Led.Red;
        }

        var count = _scanner.CompanionClientCount;
        CompanionStatus = $"{count} companion app{(count == 1 ? "" : "s")} connected";
        CompanionLed = count > 0 ? Led.Green : Led.Gray;
    }

    private static ScanRow ToRow(ScanEntry e) => new(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), e.Code);

    /// <summary>Marshal an action onto the UI thread (service events fire on network threads).</summary>
    private static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
