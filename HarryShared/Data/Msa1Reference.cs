using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarryShared.Data;

/// <summary>
/// An MSA1 reference part (the xm reference values), stored per file
/// <c>&lt;ReferencePath&gt;\&lt;Module&gt;\MSA1\&lt;Name&gt;.json</c> (2026-07-21). The milled MSA1 reference
/// parts have no readable DMC and the camera emits fake DMCs, so a part is matched to a reference by
/// BEST-MATCH of its measured means (see the MSA engine), not by DMC. <see cref="IsTemplate"/> files
/// (DEMO_… / <c>template:true</c>) are blank starter templates and are IGNORED during evaluation.
/// A measurement missing from <see cref="Values"/> is simply NOT evaluated for xm (n/a, never a fail).
/// </summary>
public sealed class Msa1Reference
{
    public const string SubfolderName = "MSA1";
    public const string TemplatePrefix = "DEMO_";

    [JsonPropertyName("module")]
    public string Module { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>True = blank starter template (not a real reference part) — ignored during evaluation.</summary>
    [JsonPropertyName("template")]
    public bool Template { get; set; }

    /// <summary>Reference value xm per measurement display name. A missing entry → not evaluated.</summary>
    [JsonPropertyName("values")]
    public Dictionary<string, double> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file this reference was loaded from (not serialized) — for transparency in the report.</summary>
    [JsonIgnore]
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>A blank template (by flag or DEMO_ file-name prefix) — never used to evaluate a part.</summary>
    [JsonIgnore]
    public bool IsTemplate =>
        Template || Path.GetFileName(SourceFile).StartsWith(TemplatePrefix, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static string FolderFor(string referenceFolder, string module) =>
        Path.Combine(referenceFolder, module, SubfolderName);

    public static string TemplatePathFor(string referenceFolder, string module) =>
        Path.Combine(FolderFor(referenceFolder, module), $"{TemplatePrefix}{module}.json");

    /// <summary>Write to <c>&lt;folder&gt;\&lt;Module&gt;\MSA1\&lt;fileName&gt;.json</c>; returns the full path.</summary>
    public string Save(string referenceFolder, string fileName)
    {
        var dir = FolderFor(referenceFolder, Module);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        return path;
    }

    /// <summary>Load every MSA1 reference file for a module (each tagged with its <see cref="SourceFile"/>).</summary>
    public static List<Msa1Reference> LoadAll(string referenceFolder, string module)
    {
        var result = new List<Msa1Reference>();
        if (string.IsNullOrWhiteSpace(referenceFolder))
            return result;
        var dir = FolderFor(referenceFolder, module);
        if (!Directory.Exists(dir))
            return result;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<Msa1Reference>(File.ReadAllText(file), Options);
                if (r is not null)
                {
                    r.SourceFile = file;
                    result.Add(r);
                }
            }
            catch { /* skip unreadable file */ }
        }
        return result;
    }

    /// <summary>The real (non-template) references usable for best-match.</summary>
    public static List<Msa1Reference> LoadCandidates(string referenceFolder, string module) =>
        LoadAll(referenceFolder, module).Where(r => !r.IsTemplate).ToList();
}
