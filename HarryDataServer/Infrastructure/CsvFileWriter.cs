using System.IO;
using System.Text;
using HarryDataServer.Services;

namespace HarryDataServer.Infrastructure;

/// <summary>
/// Writes CSV rows to a rotating file. A new file (with header) is opened on first
/// write and whenever <see cref="MaxRowsPerFile"/> is reached or
/// <see cref="Rotate"/> is called (e.g. on an order change). Not thread-safe —
/// intended to be driven by a single processor task. Reused by the diagnostic,
/// main and MSA CSV exports.
/// </summary>
public sealed class CsvFileWriter : IDisposable
{
    private readonly string _baseDir;
    private readonly bool _dateSubfolders;
    private readonly ILogService _log;
    private readonly string _delimiter;

    private IReadOnlyList<IReadOnlyList<string>> _headerRows = Array.Empty<IReadOnlyList<string>>();
    private string _fileLabel = "data";
    private StreamWriter? _writer;
    private string? _currentPath;
    private int _rowsInFile;

    public CsvFileWriter(string baseDir, int maxRowsPerFile, bool dateSubfolders, ILogService log, string delimiter = ",")
    {
        _baseDir = baseDir;
        MaxRowsPerFile = Math.Max(1, maxRowsPerFile);
        _dateSubfolders = dateSubfolders;
        _log = log;
        _delimiter = delimiter;
    }

    public int MaxRowsPerFile { get; }
    public int RowsInCurrentFile => _rowsInFile;
    public string? CurrentPath => _currentPath;

    /// <summary>Set a single header row and the file label used for the next file.</summary>
    public void Configure(IReadOnlyList<string> header, string fileLabel) =>
        Configure(new[] { header }, fileLabel);

    /// <summary>
    /// Set one or more header rows (e.g. a controller row above a parameter row)
    /// and the file label used for the next file that is opened.
    /// </summary>
    public void Configure(IReadOnlyList<IReadOnlyList<string>> headerRows, string fileLabel)
    {
        _headerRows = headerRows;
        _fileLabel = SanitizeLabel(fileLabel);
    }

    /// <summary>Write one row, opening a new file (with header) when needed.</summary>
    public void WriteRow(IReadOnlyList<string?> values)
    {
        if (_writer is null)
            Open();

        _writer!.WriteLine(string.Join(_delimiter, values.Select(Escape)));
        _rowsInFile++;

        if (_rowsInFile >= MaxRowsPerFile)
            Rotate();
    }

    public void Flush() => _writer?.Flush();

    /// <summary>Close the current file so the next write starts a fresh one.</summary>
    public void Rotate()
    {
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
        _currentPath = null;
        _rowsInFile = 0;
    }

    public void Dispose() => Rotate();

    private void Open()
    {
        var now = DateTime.Now;
        var dir = _dateSubfolders
            ? Path.Combine(_baseDir, now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"))
            : _baseDir;
        Directory.CreateDirectory(dir);

        var baseName = $"{now:yyyy-MM-dd-HH-mm-ss}-{_fileLabel}";
        var path = Path.Combine(dir, baseName + ".csv");

        // Avoid clobbering a file created in the same second.
        var counter = 1;
        while (File.Exists(path))
            path = Path.Combine(dir, $"{baseName}-{counter++}.csv");

        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = false };
        _currentPath = path;
        _rowsInFile = 0;

        foreach (var headerRow in _headerRows)
        {
            if (headerRow.Count > 0)
                _writer.WriteLine(string.Join(_delimiter, headerRow.Select(Escape)));
        }

        _log.Information("CSV file opened: {Path}", path);
    }

    private string Escape(string? value)
    {
        var v = value ?? string.Empty;
        if (v.Contains(_delimiter) || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    private static string SanitizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "data";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(label.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
