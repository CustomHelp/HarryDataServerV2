using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarryShared.Data;

/// <summary>
/// MSA reference file for one module (CLAUDE.md section 7), shared with the server's
/// <c>MsaReferenceFile</c>. Holds the reference value xm per measurement (MSA1) and the
/// prepared-error expectation per measurement (LimitSample), keyed by display name.
/// File name convention: <c>MSA_&lt;module&gt;.json</c> in the configured reference folder.
/// </summary>
public sealed class MsaReferenceFile
{
    [JsonPropertyName("module")]
    public string Module { get; set; } = string.Empty;

    [JsonPropertyName("msa_version")]
    public string MsaVersion { get; set; } = string.Empty;

    /// <summary>Reference value xm per measurement display name (MSA1).</summary>
    [JsonPropertyName("references")]
    public Dictionary<string, double> References { get; set; } = new();

    /// <summary>True = prepared error, must be rejected (LimitSample), keyed by display name.</summary>
    [JsonPropertyName("limit_sample_expected")]
    public Dictionary<string, bool> LimitSampleExpected { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static string FileName(string module) => $"MSA_{module}.json";

    /// <summary>Load the reference file for a module from a folder, or null if absent/invalid.</summary>
    public static MsaReferenceFile? Load(string referenceFolder, string module)
    {
        if (string.IsNullOrWhiteSpace(referenceFolder))
            return null;
        var path = Path.Combine(referenceFolder, FileName(module));
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<MsaReferenceFile>(File.ReadAllText(path), Options);
    }

    /// <summary>Write this reference file as MSA_&lt;module&gt;.json into the folder; returns the path.</summary>
    public string Save(string referenceFolder)
    {
        Directory.CreateDirectory(referenceFolder);
        var path = Path.Combine(referenceFolder, FileName(Module));
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        return path;
    }
}
