using System.Globalization;
using System.IO;
using HarryDataServer.Models;
using IniParser;
using IniParser.Model;

namespace HarryDataServer.Configuration;

/// <summary>
/// Reads a Collage.ini layout file (CLAUDE.md section 12) into a
/// <see cref="CollageLayout"/>. The [CollageSettings] section holds the canvas; the
/// [ImageN] sections (discovered dynamically, ordered by index) hold the placements.
/// Authored by the separate HarryCollageCreator tool; format unchanged from V1.
/// </summary>
public sealed class CollageIniReader
{
    private readonly FileIniDataParser _parser = new();

    /// <summary>Parse the Collage.ini at <paramref name="path"/>. Throws if the file is missing.</summary>
    public CollageLayout Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"Collage.ini not found at '{path}'.", path);

        IniData data = _parser.ReadFile(path);
        var settings = data["CollageSettings"];

        return new CollageLayout
        {
            CanvasWidth = Int(settings, "CanvasWidth", 320),
            CanvasHeight = Int(settings, "CanvasHeight", 650),
            BackgroundColor = Str(settings, "BackgroundColor", "White"),
            Images = ParseImages(data),
        };
    }

    private static IReadOnlyList<CollageImageSpec> ParseImages(IniData data)
    {
        var images = new List<(int Index, CollageImageSpec Spec)>();

        foreach (SectionData section in data.Sections)
        {
            if (!section.SectionName.StartsWith("Image", StringComparison.OrdinalIgnoreCase))
                continue;

            var indexText = section.SectionName.Substring("Image".Length);
            if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue; // not an [ImageN] section

            var k = section.Keys;
            var template = Str(k, "TemplateName", string.Empty);
            if (string.IsNullOrWhiteSpace(template))
                continue;

            images.Add((index, new CollageImageSpec
            {
                TemplateName = template,
                PosX = Int(k, "Pos_X", 0),
                PosY = Int(k, "Pos_Y", 0),
                Scale = Dbl(k, "Scale", 1.0),
                Zoom = Dbl(k, "Zoom", 1.0),
                CropX = Int(k, "Crop_X", 0),
                CropY = Int(k, "Crop_Y", 0),
                CropWidth = Int(k, "Crop_Width", 0),
                CropHeight = Int(k, "Crop_Height", 0),
                MirrorX = Bool(k, "Mirror_X", false),
                MirrorY = Bool(k, "Mirror_Y", false),
                KeyName = Str(k, "KeyName", string.Empty),
            }));
        }

        images.Sort((a, b) => a.Index.CompareTo(b.Index));
        return images.Select(i => i.Spec).ToList();
    }

    private static string Str(KeyDataCollection? keys, string key, string fallback)
    {
        if (keys is null || !keys.ContainsKey(key))
            return fallback;
        var value = keys[key];
        return string.IsNullOrEmpty(value) ? fallback : value.Trim();
    }

    private static int Int(KeyDataCollection? keys, string key, int fallback)
    {
        var raw = Str(keys, key, string.Empty);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static double Dbl(KeyDataCollection? keys, string key, double fallback)
    {
        var raw = Str(keys, key, string.Empty);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static bool Bool(KeyDataCollection? keys, string key, bool fallback)
    {
        var raw = Str(keys, key, string.Empty).ToLowerInvariant();
        return raw switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }
}
