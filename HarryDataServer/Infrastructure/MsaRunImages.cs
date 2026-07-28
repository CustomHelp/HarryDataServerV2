using System.IO;
using HarryDataServer.Services;

namespace HarryDataServer.Infrastructure;

/// <summary>Outcome of collecting one MSA run's images.</summary>
public readonly record struct MsaImageMoveResult(int Found, int Moved, int LeftBehind);

/// <summary>
/// Collects one MSA/LimitSample run's images out of the GoldenSample transit folder.
///
/// <para><b>Target concept (Philipp, 2026-07-28):</b> <c>05_High_Resolution_GoldenSample\Input</c> is a
/// <b>transit buffer</b>, not an archive. A finished run therefore <b>MOVES</b> its images into the
/// run folder under <c>[MSA] ReportPath</c>; whatever stays behind (aborted runs, M1X production
/// images) is aged out by <c>[Retention] Images_InputLeftovers</c>. Before 2026-07-28 this copied,
/// which left every run's originals in the transit folder forever.</para>
///
/// <para>A run's images are those whose filename <b>Serial1 starts with the 14-char BaseID</b> (the
/// loop counter + zero padding follow). The move crosses the drive boundary (Z: → X:), so it is
/// copy → size-verify → delete. If a single image cannot be moved it is a WARNING and the
/// <b>original is left in place</b> (the retention sweep removes it later) — a run is never failed
/// because of an image. Extracted from <c>MsaService</c> so this rule is unit-testable.</para>
/// </summary>
public static class MsaRunImages
{
    /// <param name="goldenSamplePath">
    /// <c>[NAS] HighResGoldenSamplePath</c>; expanded with <see cref="ImageFileName.SortedRoot"/> so
    /// the live <c>\Input</c> folder and any legacy day-folders are both searched.
    /// </param>
    /// <param name="baseId">The run's 14-char BaseID.</param>
    /// <param name="imgDir">Target folder (the run's <c>IMG\</c>).</param>
    public static MsaImageMoveResult Move(string goldenSamplePath, string baseId, string imgDir, ILogService log)
    {
        var src = ImageFileName.SortedRoot(goldenSamplePath);
        if (src is null || !Directory.Exists(src))
        {
            log.Information("MSA images for BaseID {Base}: GoldenSample source '{Src}' not available — 0 moved.",
                baseId, goldenSamplePath);
            return new MsaImageMoveResult(0, 0, 0);
        }

        var found = 0;
        var moved = 0;
        var leftBehind = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                if (!ImageFileName.MatchesBaseId(Path.GetFileName(file), baseId))
                    continue;
                found++;
                try
                {
                    Directory.CreateDirectory(imgDir);
                    var dest = Path.Combine(imgDir, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: true);

                    // Verify before removing the source — never lose a QS image on a partial copy.
                    if (new FileInfo(dest).Length != new FileInfo(file).Length)
                        throw new IOException($"size mismatch after copying to '{dest}'");

                    File.Delete(file);
                    moved++;
                }
                catch (Exception ex)
                {
                    leftBehind++;
                    log.Warning("MSA image '{File}' could not be moved for BaseID {Base}: {Message} — " +
                                "original left in place (the retention sweep will remove it).",
                        file, baseId, ex.Message);
                }
            }

            log.Information("MSA images for BaseID {Base}: {Found} found, {Moved} moved into {Dir}{Left}.",
                baseId, found, moved, imgDir,
                leftBehind > 0 ? $" ({leftBehind} left behind)" : string.Empty);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to move run images for BaseID {Base}.", baseId);
        }

        return new MsaImageMoveResult(found, moved, leftBehind);
    }
}
