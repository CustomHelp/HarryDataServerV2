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
        var serialsForLog = string.Join(", ", formattedSerials.Where(s => !string.IsNullOrWhiteSpace(s)));

        // A missing/empty source folder used to be a silent no-op — the exact reason OK low-res
        // images piled up in Z:\01\Input for days. Make it a visible WARNING instead.
        if (string.IsNullOrWhiteSpace(searchPath))
        {
            if (!string.IsNullOrEmpty(serialsForLog))
                _log.Warning("Image handling: no source folder configured; cannot clean images for [{Serials}].", serialsForLog);
            return true;
        }

        if (!Directory.Exists(searchPath))
        {
            _log.Warning("Image handling: source folder '{Path}' does not exist; cannot clean images for [{Serials}].",
                searchPath, serialsForLog);
            return true;
        }

        var files = FindFiles(formattedSerials, searchPath);
        if (files.Count == 0)
        {
            _log.Warning("Image handling: no images found in '{Path}' for [{Serials}].", searchPath, serialsForLog);
            return true;
        }

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

    /// <summary>
    /// DE (ST160): delete every image belonging to a scrapped part. DE is polymorphic on the live
    /// line — most DE telegrams carry the full frame SZID (a fully/partly assembled part being
    /// discarded, images across M1X/M5X), a few carry only the trimmer serial (a loose rejected
    /// trimmer, M2X images). We therefore purge by BOTH the frame SZID (19) and the trimmer serial
    /// (13) when present. Matching is by the normalised serial as the Field 1 PREFIX — the real
    /// filenames store Field 1 as the serial right-padded with '0' (no separators), so
    /// <c>Field1.StartsWith(serial)</c> is exact and cannot spill onto an adjacent serial. Each root
    /// is searched recursively, covering both the live <c>\Input</c> folder and the NAS-sorted
    /// <c>YYYY\MM\DD</c> day-folders. Returns the number of images deleted. Failures are logged as
    /// WARNING (never swallowed) and do not abort the sweep.
    /// </summary>
    public Task<int> DeleteBySerialsAsync(
        IReadOnlyList<string> serials, IReadOnlyList<string> imageRoots, CancellationToken ct) =>
        Task.Run(() => DeleteBySerials(serials, imageRoots), ct);

    private int DeleteBySerials(IReadOnlyList<string> serials, IReadOnlyList<string> imageRoots)
    {
        var keys = serials.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0)
            return 0;

        var deleted = 0;
        foreach (var root in imageRoots)
        {
            var sortedRoot = ImageFileName.SortedRoot(root);
            if (sortedRoot is null || !Directory.Exists(sortedRoot))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(sortedRoot, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                _log.Warning("DE: cannot enumerate '{Root}' for [{Serials}]: {Message}",
                    sortedRoot, string.Join(", ", keys), ex.Message);
                continue;
            }

            foreach (var file in files)
            {
                var field1 = ImageFileName.Field1Of(Path.GetFileName(file));
                if (field1 is null || !keys.Any(k => field1.StartsWith(k, StringComparison.OrdinalIgnoreCase)))
                    continue;
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _log.Warning("DE: failed to delete '{File}' for [{Serials}]: {Message}",
                        file, string.Join(", ", keys), ex.Message);
                }
            }
        }
        return deleted;
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

    private List<string> FindFiles(IReadOnlyList<string> formattedSerials, string searchPath)
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
        catch (Exception ex)
        {
            // Tree partially unavailable — never swallow silently; surface it so a broken NAS mount
            // or permission problem is visible instead of looking like "nothing to clean".
            _log.Warning("Image search failed in '{Path}' for [{Serials}]: {Message}",
                searchPath, string.Join(", ", serials), ex.Message);
        }
        return result;
    }
}
