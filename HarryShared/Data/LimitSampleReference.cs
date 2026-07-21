using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarryShared.Data;

/// <summary>
/// A LimitSample reference for ONE taught part, stored as its own file
/// <c>&lt;ReferencePath&gt;\&lt;Module&gt;\LimitSamples\&lt;sanitized-DMC&gt;.json</c> (2026-07-21 redesign).
/// Replaces the module-global <c>limit_sample_expected</c> block of <see cref="MsaReferenceFile"/>
/// (still read as a legacy fallback). MSA1 xm references stay module-wide in MSA_&lt;module&gt;.json.
/// Only measurements the camera actually judged (status 0/1) are stored; status-2 features are omitted.
/// </summary>
public sealed class LimitSampleReference
{
    public const string SubfolderName = "LimitSamples";

    /// <summary>Expectation values (the JSON stores the string, not a bool, for readability).</summary>
    public const string ShouldFail = "ShouldFail";
    public const string ShouldPass = "ShouldPass";

    [JsonPropertyName("dmc")]
    public string Dmc { get; set; } = string.Empty;

    [JsonPropertyName("module")]
    public string Module { get; set; } = string.Empty;

    [JsonPropertyName("taught_at")]
    public DateTime TaughtAt { get; set; }

    /// <summary>The BaseID of the teach run the part was learned from (traceability).</summary>
    [JsonPropertyName("source_base_id")]
    public string SourceBaseId { get; set; } = string.Empty;

    /// <summary>The controllers that contributed judged measurements for this part.</summary>
    [JsonPropertyName("controllers")]
    public List<string> Controllers { get; set; } = new();

    /// <summary>Measurement display name → "ShouldFail" (prepared error, must be rejected) / "ShouldPass".</summary>
    [JsonPropertyName("expected")]
    public Dictionary<string, string> Expected { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>True when the named measurement is a prepared error that must be rejected.</summary>
    public bool IsShouldFail(string displayName) =>
        Expected.TryGetValue(displayName, out var v) && string.Equals(v, ShouldFail, StringComparison.OrdinalIgnoreCase);

    /// <summary>Number of prepared errors (ShouldFail) in this part's reference.</summary>
    public int ExpectedRejectCount => Expected.Count(kv => string.Equals(kv.Value, ShouldFail, StringComparison.OrdinalIgnoreCase));

    /// <summary>The per-module LimitSamples folder <c>&lt;ReferencePath&gt;\&lt;Module&gt;\LimitSamples</c>.</summary>
    public static string FolderFor(string referenceFolder, string module) =>
        Path.Combine(referenceFolder, module, SubfolderName);

    /// <summary>Make a DMC safe as a file name (invalid chars → '_'); the original DMC stays in the JSON.</summary>
    public static string SanitizeDmc(string dmc)
    {
        if (string.IsNullOrWhiteSpace(dmc))
            return "UnknownDMC";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(dmc.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    public static string PathFor(string referenceFolder, string module, string dmc) =>
        Path.Combine(FolderFor(referenceFolder, module), SanitizeDmc(dmc) + ".json");

    /// <summary>Write this part reference; returns the full path.</summary>
    public string Save(string referenceFolder)
    {
        var dir = FolderFor(referenceFolder, Module);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SanitizeDmc(Dmc) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        return path;
    }

    /// <summary>Load one part reference by DMC, or null if absent/invalid.</summary>
    public static LimitSampleReference? Load(string referenceFolder, string module, string dmc)
    {
        if (string.IsNullOrWhiteSpace(referenceFolder))
            return null;
        var path = PathFor(referenceFolder, module, dmc);
        if (!File.Exists(path))
            return null;
        try { return JsonSerializer.Deserialize<LimitSampleReference>(File.ReadAllText(path), Options); }
        catch { return null; }
    }

    /// <summary>Load every taught part reference for a module (empty list when the folder is absent).</summary>
    public static List<LimitSampleReference> LoadAll(string referenceFolder, string module)
    {
        var result = new List<LimitSampleReference>();
        if (string.IsNullOrWhiteSpace(referenceFolder))
            return result;
        var dir = FolderFor(referenceFolder, module);
        if (!Directory.Exists(dir))
            return result;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<LimitSampleReference>(File.ReadAllText(file), Options);
                if (r is not null)
                    result.Add(r);
            }
            catch { /* skip unreadable file */ }
        }
        return result;
    }

    /// <summary>Delete one taught part's file; true when a file was removed.</summary>
    public static bool Delete(string referenceFolder, string module, string dmc)
    {
        var path = PathFor(referenceFolder, module, dmc);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }
}
