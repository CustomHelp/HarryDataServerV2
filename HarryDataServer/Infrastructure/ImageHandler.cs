using System.IO;
using HarryDataServer.Services;

namespace HarryDataServer.Infrastructure;

/// <summary>
/// Part-exit image handling (ported from the V1 logic per spec). Finds the individual
/// *.bmp images for a part (by formatted serial) under the single-images folder and
/// either deletes them, or backs them up (with a size-verify) before deleting.
/// </summary>
public sealed class ImageHandler
{
    private readonly ILogService _log;

    public ImageHandler(ILogService log) => _log = log;

    /// <param name="formattedSerials">Serials with "_" inserted after char 12 (SZID + trimmer).</param>
    public Task<bool> HandleAsync(
        IReadOnlyList<string> formattedSerials, string searchPath, bool deletePictures,
        string backupFolder, CancellationToken ct) =>
        Task.Run(() => Handle(formattedSerials, searchPath, deletePictures, backupFolder), ct);

    private bool Handle(IReadOnlyList<string> formattedSerials, string searchPath, bool deletePictures, string backupFolder)
    {
        if (string.IsNullOrWhiteSpace(searchPath) || !Directory.Exists(searchPath))
            return true; // nothing to handle

        var files = FindFiles(formattedSerials, searchPath);
        if (files.Count == 0)
            return true;

        // Backup subfolder for the whole part: BackupFolder\YYYY\MM\DD\ (SOW §5.2.3,
        // e.g. Z:\03_High_Resolution_NG\2025\07\01). No hour level.
        var now = DateTime.Now;
        var backupDir = string.IsNullOrWhiteSpace(backupFolder)
            ? null
            : Path.Combine(backupFolder, now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));

        var ok = true;
        foreach (var file in files)
        {
            try
            {
                if (deletePictures)
                {
                    File.Delete(file);
                }
                else
                {
                    BackupAndDelete(file, backupDir);
                }
            }
            catch (Exception ex)
            {
                ok = false;
                _log.Error(ex, "Image handling failed for {File}.", file);
            }
        }
        return ok;
    }

    private static void BackupAndDelete(string file, string? backupDir)
    {
        if (backupDir is null)
            throw new InvalidOperationException("BackupFolder not configured but DeletePictures=false.");

        Directory.CreateDirectory(backupDir);
        var dest = Path.Combine(backupDir, Path.GetFileName(file));
        File.Copy(file, dest, overwrite: true);

        // Verify the copy before deleting the source (file sizes must match).
        if (new FileInfo(dest).Length != new FileInfo(file).Length)
            throw new IOException($"Backup size mismatch for '{file}'.");

        File.Delete(file);
    }

    private static List<string> FindFiles(IReadOnlyList<string> formattedSerials, string searchPath)
    {
        var result = new List<string>();
        var serials = formattedSerials.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (serials.Count == 0)
            return result;

        try
        {
            foreach (var file in Directory.EnumerateFiles(searchPath, "*.bmp", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (serials.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    result.Add(file);
            }
        }
        catch
        {
            // Tree partially unavailable; caller treats absence as "nothing to do".
        }
        return result;
    }
}
