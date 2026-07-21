using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarryDataServer.Services;

namespace HarryDataServer.Configuration;

/// <summary>
/// MSA reference file for one module (CLAUDE.md section 7). Holds the reference
/// value xm per measurement (for MSA1 Cgk) and the expected pass/fail per
/// measurement (for LimitSample). Keyed by measurement display name.
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

    /// <summary>True = this measurement is a prepared error and must be rejected (LimitSample).</summary>
    [JsonPropertyName("limit_sample_expected")]
    public Dictionary<string, bool> LimitSampleExpected { get; set; } = new();
}

/// <summary>Loads <see cref="MsaReferenceFile"/>s by module from the configured folder.</summary>
public sealed class MsaReferenceLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogService _log;

    public MsaReferenceLoader(ILogService log) => _log = log;

    /// <summary>The full path of the reference file for a module (whether or not it exists).</summary>
    public static string ReferenceFilePath(string referenceFolder, string module) =>
        string.IsNullOrWhiteSpace(referenceFolder) ? string.Empty : Path.Combine(referenceFolder, $"MSA_{module}.json");

    /// <summary>Load the reference file for a module, or null if absent/invalid.</summary>
    public MsaReferenceFile? Load(string referenceFolder, string module)
    {
        if (string.IsNullOrWhiteSpace(referenceFolder))
            return null;

        // Always log the FULL resolved path so "not loaded" issues are diagnosable (task 1).
        var path = Path.GetFullPath(Path.Combine(referenceFolder, $"MSA_{module}.json"));
        if (!File.Exists(path))
        {
            _log.Warning("MSA reference for {Module}: file NOT FOUND at {Path} (resolved from ReferencePath '{Folder}').",
                module, path, referenceFolder);
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<MsaReferenceFile>(json, Options);
            if (file is not null)
            {
                var expectedRejects = file.LimitSampleExpected.Count(kv => kv.Value);
                _log.Information(
                    "MSA reference for {Module}: LOADED from {Path} — {Refs} xm reference(s), {Entries} limit-sample entrie(s) ({Rejects} expected reject(s)).",
                    module, path, file.References.Count, file.LimitSampleExpected.Count, expectedRejects);
            }
            else
            {
                _log.Warning("MSA reference for {Module}: file {Path} parsed to null.", module, path);
            }
            return file;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "MSA reference for {Module}: FAILED to parse {Path}.", module, path);
            return null;
        }
    }
}
