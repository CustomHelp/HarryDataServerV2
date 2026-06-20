using System.Globalization;
using System.IO;
using System.Text;
using IniParser;
using IniParser.Model;

namespace HarryCollageCreator;

/// <summary>The canvas-level settings plus the authored image slots.</summary>
public sealed class CollageDocument
{
    public int CanvasWidth { get; set; } = 320;
    public int CanvasHeight { get; set; } = 650;
    public string BackgroundColor { get; set; } = "White";
    public List<ImageSlot> Slots { get; } = new();
}

/// <summary>
/// Reads/writes Collage.ini in the exact format the server's <c>CollageIniReader</c>
/// expects: [CollageSettings] (CanvasWidth/Height/BackgroundColor) and ordered
/// [ImageN] sections (TemplateName, Pos_X/Y, Scale, Zoom, Crop_X/Y/Width/Height,
/// Mirror_X/Y, KeyName).
/// </summary>
public static class CollageIniIo
{
    public static void Save(string path, CollageDocument doc)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("[CollageSettings]");
        sb.AppendLine($"CanvasWidth={doc.CanvasWidth}");
        sb.AppendLine($"CanvasHeight={doc.CanvasHeight}");
        sb.AppendLine($"BackgroundColor={doc.BackgroundColor}");
        sb.AppendLine();

        for (var i = 0; i < doc.Slots.Count; i++)
        {
            var s = doc.Slots[i];
            sb.AppendLine($"[Image{i + 1}]");
            sb.AppendLine($"TemplateName={s.TemplateName}");
            sb.AppendLine($"Pos_X={s.PosX}");
            sb.AppendLine($"Pos_Y={s.PosY}");
            sb.AppendLine($"Scale={s.Scale.ToString(inv)}");
            sb.AppendLine($"Zoom={s.Zoom.ToString(inv)}");
            sb.AppendLine($"Crop_X={s.CropX}");
            sb.AppendLine($"Crop_Y={s.CropY}");
            sb.AppendLine($"Crop_Width={s.CropWidth}");
            sb.AppendLine($"Crop_Height={s.CropHeight}");
            sb.AppendLine($"Mirror_X={(s.MirrorX ? "true" : "false")}");
            sb.AppendLine($"Mirror_Y={(s.MirrorY ? "true" : "false")}");
            sb.AppendLine($"KeyName={s.KeyName}");
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public static CollageDocument Load(string path)
    {
        var data = new FileIniDataParser().ReadFile(path);
        var settings = data["CollageSettings"];
        var doc = new CollageDocument
        {
            CanvasWidth = Int(settings, "CanvasWidth", 320),
            CanvasHeight = Int(settings, "CanvasHeight", 650),
            BackgroundColor = Str(settings, "BackgroundColor", "White"),
        };

        var images = new List<(int Index, ImageSlot Slot)>();
        foreach (SectionData section in data.Sections)
        {
            if (!section.SectionName.StartsWith("Image", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!int.TryParse(section.SectionName.Substring("Image".Length),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue;

            var k = section.Keys;
            images.Add((index, new ImageSlot
            {
                TemplateName = Str(k, "TemplateName", string.Empty),
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
        doc.Slots.AddRange(images.Select(i => i.Slot));
        return doc;
    }

    private static string Str(KeyDataCollection? k, string key, string fallback)
    {
        if (k is null || !k.ContainsKey(key)) return fallback;
        var v = k[key];
        return string.IsNullOrEmpty(v) ? fallback : v.Trim();
    }

    private static int Int(KeyDataCollection? k, string key, int fallback)
    {
        var raw = Str(k, key, string.Empty);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static double Dbl(KeyDataCollection? k, string key, double fallback)
    {
        var raw = Str(k, key, string.Empty);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static bool Bool(KeyDataCollection? k, string key, bool fallback)
    {
        var raw = Str(k, key, string.Empty).ToLowerInvariant();
        return raw switch { "true" or "1" or "yes" or "on" => true, "false" or "0" or "no" or "off" => false, _ => fallback };
    }
}
