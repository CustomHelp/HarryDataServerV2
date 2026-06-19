using System.IO;
using System.Text.Json;
using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.Configuration;

/// <summary>
/// Loads the per-camera JSON template files (Result_*.json / Settings_*.json)
/// referenced from Harry.ini and parses them into strongly-typed DTOs.
/// Missing files are logged and skipped — a camera can run with only a result
/// template, or only a settings template.
/// </summary>
public sealed class JsonTemplateLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Folder next to the executable that templates are deployed into.</summary>
    private static readonly string LocalTemplateDir = Path.Combine(AppContext.BaseDirectory, "Templates");

    private readonly ILogService _log;

    public JsonTemplateLoader(ILogService log) => _log = log;

    /// <summary>
    /// Resolve a template path: use the configured path if it exists, otherwise
    /// fall back to a same-named file in the local <c>Templates\</c> folder (and
    /// finally the executable directory). Returns null if nothing is found.
    /// This lets absolute production paths work while keeping dev/test runs
    /// functional when those paths are absent.
    /// </summary>
    private string? ResolveTemplatePath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        var fileName = Path.GetFileName(configuredPath);

        var localTemplates = Path.Combine(LocalTemplateDir, fileName);
        if (File.Exists(localTemplates))
        {
            _log.Debug("Template {File} not at configured path; using {Fallback}.", fileName, localTemplates);
            return localTemplates;
        }

        var localBase = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(localBase))
        {
            _log.Debug("Template {File} not at configured path; using {Fallback}.", fileName, localBase);
            return localBase;
        }

        return null;
    }

    /// <summary>Load and parse a single Result_*.json file. Returns null if missing/invalid.</summary>
    public ResultTemplateFile? LoadResultTemplate(string configuredPath)
    {
        var path = ResolveTemplatePath(configuredPath);
        if (path is null)
        {
            _log.Warning("Result template not found (configured: {Path}).", configuredPath);
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var template = JsonSerializer.Deserialize<ResultTemplateFile>(json, Options);
            if (template is null)
            {
                _log.Warning("Result template parsed to null: {Path}", path);
                return null;
            }

            _log.Debug("Loaded result template {Camera} with {Count} measurements from {Path}",
                template.Camera, template.Measurements.Count, path);
            return template;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to parse result template: {Path}", path);
            return null;
        }
    }

    /// <summary>Load and parse a single Settings_*.json file. Returns null if missing/invalid.</summary>
    public SettingsTemplateFile? LoadSettingsTemplate(string configuredPath)
    {
        var path = ResolveTemplatePath(configuredPath);
        if (path is null)
        {
            _log.Warning("Settings template not found (configured: {Path}).", configuredPath);
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var template = JsonSerializer.Deserialize<SettingsTemplateFile>(json, Options);
            if (template is null)
            {
                _log.Warning("Settings template parsed to null: {Path}", path);
                return null;
            }

            _log.Debug("Loaded settings template {Camera} with {Count} settings from {Path}",
                template.Camera, template.Settings.Count, path);
            return template;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to parse settings template: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Load both templates for every configured camera, keyed by camera name.
    /// </summary>
    public IReadOnlyDictionary<string, CameraTemplates> LoadAll(IEnumerable<CameraConfig> cameras)
    {
        var result = new Dictionary<string, CameraTemplates>(StringComparer.OrdinalIgnoreCase);

        foreach (var camera in cameras)
        {
            var templates = new CameraTemplates
            {
                CameraName = camera.CameraName,
                Result = LoadResultTemplate(camera.JsonParameters),
                Settings = LoadSettingsTemplate(camera.JsonSettings),
            };
            result[camera.CameraName] = templates;
        }

        _log.Information("Loaded JSON templates for {Count} camera(s).", result.Count);
        return result;
    }
}
