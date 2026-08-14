namespace HarryDataServer.Services;

/// <summary>
/// Free-disk watchdog ([Monitoring] section). Checks every drive the server actually uses — the
/// MySQL <c>tmpdir</c>/<c>datadir</c> (asked of the running server, not guessed), the log folder,
/// the CSV roots, the MSA report roots and the NAS image folders — and reports a drive running out
/// of space as a WARNING (and below the critical threshold as an ERROR) before it breaks anything.
///
/// <para>Why this exists: on 2026-08-14 C: was full (0.09 GB of 343 GB, filled by Keyence
/// VisionTerminal SD-card mirrors). MySQL's tmpdir sits on C:, so only queries big enough to spill a
/// temp table to disk failed — the MSA raw-data export of two M50 runs was lost while every small
/// query kept working. Nothing in the software pointed at the disk; the symptom looked like an MSA
/// bug. One periodic WARNING would have named the cause immediately.</para>
/// </summary>
public interface IDiskSpaceMonitor
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
