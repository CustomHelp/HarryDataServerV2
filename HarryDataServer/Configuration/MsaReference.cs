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

    /// <summary>Load the reference file for a module, or null if absent/invalid.</summary>
    public MsaReferenceFile? Load(string referenceFolder, string module)
    {
        if (string.IsNullOrWhiteSpace(referenceFolder))
            return null;

        var path = Path.Combine(referenceFolder, $"MSA_{module}.json");
        if (!File.Exists(path))
        {
            _log.Warning("MSA reference file not found for module {Module}: {Path}", module, path);
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<MsaReferenceFile>(json, Options);
            if (file is not null)
                _log.Information("Loaded MSA reference for {Module} ({Count} references).",
                    module, file.References.Count);
            return file;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to parse MSA reference file {Path}", path);
            return null;
        }
    }
}
