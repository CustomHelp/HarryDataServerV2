using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarryShared.Config;
using HarryShared.Data;
using Microsoft.Win32;

namespace HarryAnalysis;

/// <summary>
/// Drives the scanner view: the operator scans a DMC (or types a serial), the tool
/// loads the part header from <c>dmcserial</c> plus every measurement (value, limits,
/// result) and shows them in a grid that can be exported to CSV.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly QueryService _query;
    private readonly HarryConfig _config;

    public MainViewModel(QueryService query, HarryConfig config)
    {
        _query = query;
        _config = config;
        ConfigFile = config.IniPath;
    }

    public string AppName => "HarryAnalysis — Part Inspector";
    public string AppVersion => "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "2.0.0");
    public string ConfigFile { get; }

    public ObservableCollection<PartMeasurementRow> Measurements { get; } = new();

    [ObservableProperty] private string _scanText = string.Empty;
    [ObservableProperty] private string _statusMessage = "Scan a DMC or enter a serial number, then press Enter.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private PartInfo? _part;

    /// <summary>Header card text built from the dmcserial row.</summary>
    public string HeaderInfo
    {
        get
        {
            if (Part is null)
                return "No part loaded.";
            var p = Part;
            return string.Join("    ",
                $"Serial: {p.SerialNumber}",
                $"Trimmer: {p.SerialTrimmer ?? "-"}",
                $"DMC: {p.Dmc ?? "-"}",
                $"Order: {p.OrderName ?? "-"}",
                $"Result: {p.ResultText}",
                $"M1x: mod {p.M1xModule?.ToString() ?? "-"}/nest {p.M1xNest?.ToString() ?? "-"}",
                $"M3x: {p.M3xModule ?? "-"}/{p.M3xNest ?? "-"}",
                $"M50 nest: {p.M50Nest ?? "-"}",
                $"Humidity: {p.M1xHumidity?.ToString("0.0") ?? "-"}",
                $"Created: {p.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        }
    }

    public string ResultBadge => Part?.ResultText ?? string.Empty;
    public bool IsOk => Part?.ResultStatus == 1;

    partial void OnPartChanged(PartInfo? value)
    {
        OnPropertyChanged(nameof(HeaderInfo));
        OnPropertyChanged(nameof(ResultBadge));
        OnPropertyChanged(nameof(IsOk));
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var scan = ScanText.Trim();
        if (scan.Length == 0)
        {
            StatusMessage = "Enter a DMC or serial number first.";
            return;
        }

        StatusMessage = $"Searching for '{scan}' …";
        Measurements.Clear();
        Part = null;

        try
        {
            var part = await _query.FindPartAsync(scan);
            if (part is null)
            {
                StatusMessage = $"No part found for '{scan}'.";
                return;
            }

            Part = part;
            var rows = await _query.GetPartMeasurementsAsync(part);
            foreach (var row in rows)
                Measurements.Add(row);

            var ng = rows.Count(r => r.ResultStatus == 0);
            StatusMessage = $"Loaded {rows.Count} measurement(s) — {ng} NG. Result: {part.ResultText}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Query failed: " + ex.Message;
        }
    }

    private bool CanExport => Part is not null && Measurements.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        if (Part is null)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = CsvExport.TimestampedName($"HarryAnalysis_{Sanitize(Part.SerialNumber)}"),
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var header = new[] { "Measurement", "Camera", "Module", "FeatureGroup", "Value", "Min", "Max", "Result", "MeasuredAt" };
            var rows = Measurements.Select(m => new string?[]
            {
                m.DisplayName, m.CameraName, m.Module, m.FeatureGroup,
                m.ValueText, m.MinText, m.MaxText, m.ResultText,
                m.MeasuredAt.ToString("yyyy-MM-dd HH:mm:ss"),
            });
            CsvExport.Write(dialog.FileName, header, rows);
            StatusMessage = $"Exported {Measurements.Count} rows to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Export failed: " + ex.Message;
            MessageBox.Show(ex.Message, "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
}
