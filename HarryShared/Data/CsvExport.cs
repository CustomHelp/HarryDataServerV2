using System.Globalization;
using System.IO;
using System.Text;

namespace HarryShared.Data;

/// <summary>Minimal RFC-4180 CSV writer used by the companion tools' "Export to CSV" buttons.</summary>
public static class CsvExport
{
    /// <summary>Write <paramref name="rows"/> with the given header to <paramref name="path"/> (semicolon-separated).</summary>
    public static void Write(string path, IEnumerable<string> header, IEnumerable<IEnumerable<string?>> rows)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', header.Select(Escape)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(';', row.Select(Escape)));

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Default timestamped filename, e.g. "HarryAnalysis_2026-06-20_14-05.csv".</summary>
    public static string TimestampedName(string prefix) =>
        $"{prefix}_{DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.csv";

    private static string Escape(string? field)
    {
        field ??= string.Empty;
        if (field.Contains(';') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            return '"' + field.Replace("\"", "\"\"") + '"';
        return field;
    }
}
