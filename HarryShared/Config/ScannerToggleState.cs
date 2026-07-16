using System;
using System.IO;

namespace HarryShared.Config
{
    /// <summary>
    /// Persists the per-app "scanner active" toggle across restarts, mirroring the
    /// <see cref="HarryShared.Theming.ThemeManager"/> pattern (a small text file under
    /// <c>%LOCALAPPDATA%\HarrySuite\</c>). The <c>HarrySuite</c> folder is shared suite-wide, so the
    /// state file is namespaced per app (<c>&lt;app&gt;-scanner-active.txt</c>) — each tool keeps its
    /// own toggle. All I/O is best-effort: a read/write failure falls back to the default rather
    /// than throwing.
    /// </summary>
    public static class ScannerToggleState
    {
        private static string PathFor(string app) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HarrySuite", $"{app}-scanner-active.txt");

        /// <summary>Read the persisted toggle for <paramref name="app"/>, or <paramref name="fallback"/> if none saved.</summary>
        public static bool Load(string app, bool fallback = true)
        {
            try
            {
                var path = PathFor(app);
                if (!File.Exists(path))
                    return fallback;
                var text = File.ReadAllText(path).Trim();
                return text switch
                {
                    "1" or "true" or "True" => true,
                    "0" or "false" or "False" => false,
                    _ => fallback,
                };
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Persist the toggle for <paramref name="app"/> (best-effort).</summary>
        public static void Save(string app, bool active)
        {
            try
            {
                var path = PathFor(app);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, active ? "1" : "0");
            }
            catch
            {
                /* persistence is best-effort */
            }
        }
    }
}
