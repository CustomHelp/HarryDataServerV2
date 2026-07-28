using System.IO;
using HarryDataServer.Services;

namespace HarryDataServer.Infrastructure;

/// <summary>What to do with the part's low-res images at part exit.</summary>
public enum PartImageAction
{
    /// <summary>Delete them without a replacement (NG, DE, Unknown, and OK when a collage was made).</summary>
    Delete,

    /// <summary>Move them into the backup tree: copy → size-verify → delete the source (OK without collage).</summary>
    MoveToBackup,
}

/// <summary>
/// Outcome of one part-exit image sweep. <see cref="Inspected"/>, <see cref="MsaSkipped"/> and
/// <see cref="Keys"/> exist so a "found nothing" is always diagnosable (search key vs. filename)
/// instead of a bare zero, and <see cref="Failed"/> so a partial failure can fail the ACK.
/// </summary>
public readonly record struct ImageActionResult(
    int Handled, int Failed, int Inspected, int MsaSkipped, IReadOnlyList<string> Keys);

/// <summary>
/// THE part-exit image machinery — one search/delete implementation for every part state
/// (OK / NG / DE / Unknown), so the four flows can never drift apart again (they used to differ in
/// search root, extension filter and match semantics).
///
/// <para><b>Search:</b> always the low-res individual tree only
/// (<c>[NAS] LowResIndividualPath</c>, expanded with <see cref="ImageFileName.SortedRoot"/> so the
/// live <c>\Input</c> folder AND any legacy <c>JJJJ\MM\TT</c> day-folders are covered). The NG (03),
/// diagnostic (04) and GoldenSample (05) trees are deliberately NOT touched by the part-exit flows —
/// they are owned by the camera and the retention service (CLAUDE.md §11).</para>
///
/// <para><b>Match:</b> field-accurate — a normalised serial
/// (<see cref="SerialNumberHelper.ToImageSearchKey"/>) must be a prefix of the filename's
/// <b>Serial1</b>. No extension filter, so <c>*.bmp</c> and <c>*.png</c> are treated alike.</para>
///
/// <para><b>Never touched:</b> MSA images (Serial2 carries a DMC → QS evidence) and filenames that
/// cannot be parsed. Both are counted and reported, never deleted.</para>
/// </summary>
public sealed class ImageHandler
{
    private readonly ILogService _log;

    public ImageHandler(ILogService log) => _log = log;

    /// <summary>
    /// Apply <paramref name="action"/> to every low-res image of the part.
    /// </summary>
    /// <param name="context">Part state for the log lines ("OK", "NG", "DE", "Unknown").</param>
    /// <param name="serials">
    /// The part's serials (frame SZID 19 and/or trimmer 13), already normalised to their kind length.
    /// They are re-sanitised here — immediately before the search — so a decorated value can never
    /// make the search silently miss every file.
    /// </param>
    /// <param name="lowResPath">The low-res individual path from the config (…\Input).</param>
    /// <param name="backupFolder">Backup root; required for <see cref="PartImageAction.MoveToBackup"/>.</param>
    public Task<ImageActionResult> ApplyAsync(
        string context, IReadOnlyList<string> serials, string lowResPath,
        PartImageAction action, string backupFolder, CancellationToken ct) =>
        Task.Run(() => Apply(context, serials, lowResPath, action, backupFolder), ct);

    private ImageActionResult Apply(
        string context, IReadOnlyList<string> serials, string lowResPath,
        PartImageAction action, string backupFolder)
    {
        var rawForLog = string.Join(", ", serials.Where(s => !string.IsNullOrWhiteSpace(s)));
        var keys = serials
            .Select(SerialNumberHelper.ToImageSearchKey)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keys.Count == 0)
        {
            // No serial at all — nothing can be matched. Say so instead of looking like "nothing to do".
            _log.Warning("{Context} image handling: no usable serial (raw [{Raw}]); no image touched.",
                context, rawForLog);
            return new ImageActionResult(0, 0, 0, 0, keys);
        }

        var root = ImageFileName.SortedRoot(lowResPath);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _log.Warning("{Context} image handling: low-res folder '{Path}' not available; cannot clean images for [{Keys}].",
                context, root ?? lowResPath, string.Join(", ", keys));
            return new ImageActionResult(0, 0, 0, 0, keys);
        }

        // Backup subfolder for the whole part: BackupFolder\YYYY\MM\DD\ (SOW §5.2.3). No hour level.
        string? backupDir = null;
        if (action == PartImageAction.MoveToBackup)
        {
            if (string.IsNullOrWhiteSpace(backupFolder))
            {
                _log.Error("{Context} image handling: [NAS] BackupFolder is not configured, but the images must be " +
                           "moved there (no collage). Images are KEPT — configure BackupFolder.", context);
                return new ImageActionResult(0, 1, 0, 0, keys);
            }
            var now = DateTime.Now;
            backupDir = Path.Combine(backupFolder, now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"));
        }

        var handled = 0;
        var failed = 0;
        var stats = new ScanStats();

        IEnumerable<string> files;
        try
        {
            // No extension filter: the modules write *.bmp (low-res) and *.png alike.
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            // Tree partially unavailable — never swallow; a broken NAS mount must not look like
            // "nothing to clean".
            _log.Warning("{Context} image search failed in '{Path}' for [{Keys}]: {Message}",
                context, root, string.Join(", ", keys), ex.Message);
            return new ImageActionResult(0, 1, 0, 0, keys);
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            stats.Inspected++;

            var parsed = ImageFileName.TryParse(name);
            if (parsed is null)
            {
                stats.NoteUnparsed(name);
                continue;
            }
            if (parsed.IsMsa)
            {
                // QS evidence of an MSA/LimitSample run — never removed by a production flow.
                stats.MsaSkipped++;
                continue;
            }
            if (!keys.Any(parsed.MatchesSerial1Prefix))
                continue;

            try
            {
                if (action == PartImageAction.MoveToBackup)
                    MoveToBackup(file, backupDir!);
                else
                    File.Delete(file);
                handled++;
            }
            catch (Exception ex)
            {
                failed++;
                _log.Error(ex, "{Context} image handling failed for {File}.", context, file);
            }
        }

        ReportUnparsed(context, root, stats);

        // Deletion is irreversible → always report count + keys, never silently.
        if (handled > 0)
            _log.Information("{Context}: {Count} low-res image(s) {Verb} for [{Keys}]{Target}.",
                context, handled,
                action == PartImageAction.MoveToBackup ? "moved to backup" : "deleted",
                string.Join(", ", keys),
                action == PartImageAction.MoveToBackup ? $" → {backupDir}" : string.Empty);
        else
            _log.Warning("{Context}: no images found in '{Path}' for raw [{Raw}] / key [{Keys}] " +
                         "({Inspected} file(s) inspected, {Msa} MSA image(s) skipped).",
                context, root, rawForLog, string.Join(", ", keys), stats.Inspected, stats.MsaSkipped);

        return new ImageActionResult(handled, failed, stats.Inspected, stats.MsaSkipped, keys);
    }

    /// <summary>
    /// Move one image into the backup tree across the drive/share boundary: copy, verify the size,
    /// then delete the source. A size mismatch throws, so the source is never lost on a partial copy.
    /// </summary>
    private static void MoveToBackup(string file, string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        var dest = Path.Combine(backupDir, Path.GetFileName(file));
        File.Copy(file, dest, overwrite: true);

        if (new FileInfo(dest).Length != new FileInfo(file).Length)
            throw new IOException($"Backup size mismatch for '{file}'.");

        File.Delete(file);
    }

    /// <summary>Counters of one sweep, so "found nothing" is always explainable.</summary>
    private sealed class ScanStats
    {
        public int Inspected;
        public int MsaSkipped;
        public int Unparsed;
        public string? FirstUnparsed;

        public void NoteUnparsed(string name)
        {
            Unparsed++;
            FirstUnparsed ??= name;
        }
    }

    /// <summary>
    /// Report anything a sweep could not interpret. An unknown filename is never deleted, but it must
    /// not disappear silently either — a camera writing a new layout has to be visible in the log.
    /// One line per sweep (not per file), with the count and one example.
    /// </summary>
    private void ReportUnparsed(string context, string path, ScanStats stats)
    {
        if (stats.Unparsed > 0)
            _log.Warning("{Context}: {Count} file name(s) in '{Path}' do not match the camera filename spec " +
                         "and were skipped (first: '{Example}').",
                context, stats.Unparsed, path, stats.FirstUnparsed);
    }
}
