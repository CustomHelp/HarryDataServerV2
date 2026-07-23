using System.IO;
using System.Text.Json;

namespace HarryShared.Config;

/// <summary>
/// Shared, customer-tolerant locator for the companion tools' <c>Harry.ini</c> (CLAUDE.md task 3).
/// Search order (first existing wins):
/// <list type="number">
///   <item><c>HARRY_CONFIG_DIR</c> env var (advanced override, kept for compatibility)</item>
///   <item>a per-tool override saved under <c>%APPDATA%\&lt;Tool&gt;\config.json</c> (chosen via the dialog)</item>
///   <item><c>F:\002_Configs\Harry.ini</c> (the on-line default)</item>
///   <item><c>Harry.ini</c> next to the exe (customer ZIP default)</item>
///   <item>legacy <c>D:\HarryDataServer\Harry.ini</c> (back-compat only; D: is the DVD on the line)</item>
/// </list>
/// When nothing is found the caller shows <see cref="ConfigPathDialog"/> and persists the pick here,
/// so a customer PC without <c>F:</c> can point each tool at its own config without editing files.
/// </summary>
public static class ConfigLocator
{
    public const string DefaultConfigDir = @"F:\002_Configs";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class OverrideFile
    {
        public string? HarryIni { get; set; }
    }

    /// <summary>Path of the per-tool override file (%APPDATA%\&lt;Tool&gt;\config.json).</summary>
    public static string OverridePath(string toolName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), toolName, "config.json");

    /// <summary>The Harry.ini path a tool has been pinned to, or null when none is saved/valid.</summary>
    public static string? ReadOverride(string toolName)
    {
        try
        {
            var file = OverridePath(toolName);
            if (!File.Exists(file))
                return null;
            var o = JsonSerializer.Deserialize<OverrideFile>(File.ReadAllText(file), JsonOptions);
            return string.IsNullOrWhiteSpace(o?.HarryIni) ? null : o!.HarryIni;
        }
        catch
        {
            return null; // corrupt/unreadable override is ignored, never fatal
        }
    }

    /// <summary>Pin a tool to a specific Harry.ini (best-effort persistence).</summary>
    public static void SaveOverride(string toolName, string harryIniPath)
    {
        try
        {
            var file = OverridePath(toolName);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(new OverrideFile { HarryIni = harryIniPath }, JsonOptions));
        }
        catch
        {
            // persistence is best-effort — never crash a tool because the override could not be written
        }
    }

    /// <summary>Remove a tool's pinned override (falls back to the default search order).</summary>
    public static void ClearOverride(string toolName)
    {
        try
        {
            var file = OverridePath(toolName);
            if (File.Exists(file))
                File.Delete(file);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Ordered candidate Harry.ini paths. <paramref name="toolName"/> null skips the per-tool override.</summary>
    public static IReadOnlyList<string> Candidates(string? toolName)
    {
        var list = new List<string>();

        var env = Environment.GetEnvironmentVariable("HARRY_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            list.Add(Path.Combine(env, "Harry.ini"));

        if (toolName is not null)
        {
            var ov = ReadOverride(toolName);
            if (ov is not null)
                list.Add(ov);
        }

        list.Add(Path.Combine(DefaultConfigDir, "Harry.ini"));
        list.Add(Path.Combine(AppContext.BaseDirectory, "Harry.ini"));
        list.Add(@"D:\HarryDataServer\Harry.ini");
        return list;
    }

    /// <summary>First existing candidate, or null when none exist yet.</summary>
    public static string? Resolve(string? toolName) => Candidates(toolName).FirstOrDefault(File.Exists);

    /// <summary>Resolved path, or the highest-priority would-be path (for display / error text).</summary>
    public static string ActivePath(string? toolName) =>
        Resolve(toolName)
        ?? Candidates(toolName).FirstOrDefault()
        ?? Path.Combine(DefaultConfigDir, "Harry.ini");
}
