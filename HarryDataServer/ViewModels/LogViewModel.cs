using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>One displayed log line with a level-based colour.</summary>
public sealed class LogEntryVm
{
    public required string Text { get; init; }
    public required Brush Brush { get; init; }
}

/// <summary>
/// Log tab: level + source filters (toggles), colour coding, and export of the current
/// filtered view to a .txt file under the configured LogFilePath. Backed by the
/// in-memory ring buffer (max 1000 entries).
/// </summary>
public sealed partial class LogViewModel : ObservableObject
{
    private static readonly Brush InfoBrush = Freeze(0xFF, 0xFF, 0xFF);   // white
    private static readonly Brush WarnBrush = Freeze(0xF5, 0x9E, 0x0B);   // amber
    private static readonly Brush ErrBrush = Freeze(0xEF, 0x44, 0x44);    // red

    private readonly ILogBuffer _buffer;
    private readonly string _logPath;
    private volatile bool _dirty = true;

    public LogViewModel(ILogBuffer buffer, IConfigService config)
    {
        _buffer = buffer;
        _logPath = config.Config.General.LogFilePath;
        _buffer.Changed += () => _dirty = true;

        AllCommand = new RelayCommand(() => { ShowInfo = ShowWarning = ShowError = true; });
        ExportCommand = new RelayCommand(Export);
    }

    public ObservableCollection<LogEntryVm> Entries { get; } = new();
    public IRelayCommand AllCommand { get; }
    public IRelayCommand ExportCommand { get; }

    // Level filters
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showWarning = true;
    [ObservableProperty] private bool _showError = true;

    // Source filters (multi-select)
    [ObservableProperty] private bool _showCamera = true;
    [ObservableProperty] private bool _showSps = true;
    [ObservableProperty] private bool _showDatabase = true;
    [ObservableProperty] private bool _showCsv = true;
    [ObservableProperty] private bool _showCollage = true;
    [ObservableProperty] private bool _showMsa = true;
    [ObservableProperty] private bool _showSystem = true;

    partial void OnShowInfoChanged(bool value) => _dirty = true;
    partial void OnShowWarningChanged(bool value) => _dirty = true;
    partial void OnShowErrorChanged(bool value) => _dirty = true;
    partial void OnShowCameraChanged(bool value) => _dirty = true;
    partial void OnShowSpsChanged(bool value) => _dirty = true;
    partial void OnShowDatabaseChanged(bool value) => _dirty = true;
    partial void OnShowCsvChanged(bool value) => _dirty = true;
    partial void OnShowCollageChanged(bool value) => _dirty = true;
    partial void OnShowMsaChanged(bool value) => _dirty = true;
    partial void OnShowSystemChanged(bool value) => _dirty = true;

    /// <summary>Rebuild the filtered view if the buffer or a filter changed (called on the UI timer).</summary>
    public void Tick()
    {
        if (!_dirty)
            return;
        _dirty = false;

        Entries.Clear();
        var snapshot = _buffer.Snapshot(); // oldest-first → newest at the bottom (console/chat style)
        foreach (var e in snapshot)
        {
            if (!LevelAllowed(e.Level) || !SourceAllowed(e.Source))
                continue;
            Entries.Add(new LogEntryVm { Text = e.Text, Brush = BrushFor(e.Level) });
        }
    }

    private bool LevelAllowed(LogLevelKind level) => level switch
    {
        LogLevelKind.Info => ShowInfo,
        LogLevelKind.Warning => ShowWarning,
        LogLevelKind.Error => ShowError,
        _ => true,
    };

    private bool SourceAllowed(LogSource source) => source switch
    {
        LogSource.Camera => ShowCamera,
        LogSource.Sps => ShowSps,
        LogSource.Database => ShowDatabase,
        LogSource.Csv => ShowCsv,
        LogSource.Collage => ShowCollage,
        LogSource.Msa => ShowMsa,
        LogSource.System => ShowSystem,
        _ => true,
    };

    private static Brush BrushFor(LogLevelKind level) => level switch
    {
        LogLevelKind.Warning => WarnBrush,
        LogLevelKind.Error => ErrBrush,
        _ => InfoBrush,
    };

    private void Export()
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(_logPath) ? AppContext.BaseDirectory : _logPath;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"Log_Export_{DateTime.Now:ddMMyy_HHmmss}.txt");

            var sb = new StringBuilder();
            foreach (var entry in Entries)
                sb.AppendLine(entry.Text);

            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // Export failure is non-critical; surfaced via the log file itself.
        }
    }

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
