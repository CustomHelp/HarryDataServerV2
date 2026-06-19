using System.Text.Json.Serialization;

namespace HarryDataServer.Configuration;

/// <summary>
/// DTO for a Result_*.json template file (see CLAUDE.md section 9). Describes the
/// measurement layout of a single camera's "Results" telegram.
/// </summary>
public sealed class ResultTemplateFile
{
    [JsonPropertyName("camera")]
    public string Camera { get; set; } = string.Empty;

    [JsonPropertyName("signal_word")]
    public string SignalWord { get; set; } = "Results";

    [JsonPropertyName("measurements")]
    public List<MeasurementTemplateEntry> Measurements { get; set; } = new();
}

public sealed class MeasurementTemplateEntry
{
    [JsonPropertyName("telegram_place")]
    public int TelegramPlace { get; set; }

    [JsonPropertyName("variable_name")]
    public string VariableName { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>"Result" or "Value".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>"SINT" or "Float".</summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("parameter_set")]
    public int ParameterSet { get; set; }

    [JsonPropertyName("module_ref")]
    public string ModuleRef { get; set; } = "NoRef";

    [JsonPropertyName("feature_group")]
    public string FeatureGroup { get; set; } = "NoGroup";
}

/// <summary>
/// DTO for a Settings_*.json template file. Describes the Min/Max limit layout of
/// a single camera's "Settings" telegram.
/// </summary>
public sealed class SettingsTemplateFile
{
    [JsonPropertyName("camera")]
    public string Camera { get; set; } = string.Empty;

    [JsonPropertyName("signal_word")]
    public string SignalWord { get; set; } = "Settings";

    [JsonPropertyName("settings")]
    public List<SettingTemplateEntry> Settings { get; set; } = new();
}

public sealed class SettingTemplateEntry
{
    [JsonPropertyName("telegram_place")]
    public int TelegramPlace { get; set; }

    [JsonPropertyName("setting_name")]
    public string SettingName { get; set; } = string.Empty;

    [JsonPropertyName("parameter_set")]
    public int ParameterSet { get; set; }

    /// <summary>"Min" or "Max".</summary>
    [JsonPropertyName("limit_type")]
    public string LimitType { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = "Float";
}

/// <summary>
/// The pair of templates that belong to one camera, plus the camera name they
/// were loaded for. Either template may be null if its file was missing.
/// </summary>
public sealed class CameraTemplates
{
    public required string CameraName { get; init; }
    public ResultTemplateFile? Result { get; init; }
    public SettingsTemplateFile? Settings { get; init; }
}
