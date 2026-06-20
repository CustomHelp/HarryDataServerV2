using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using HarryDataServer.Models;

namespace HarryDataServer.Infrastructure;

/// <summary>
/// Composes a single collage from individual camera images using GDI+ (CLAUDE.md
/// section 12). Runs entirely off the UI thread; one collage is built at a time.
///
/// Compositing semantics (documented assumptions — to verify against a V1 collage):
///   • Pos_X/Pos_Y address the image <b>centre</b> on the canvas.
///   • Effective draw size = crop size × Scale × Zoom (the two factors multiply).
///   • Crop is taken from the source first, then scaled, then mirrored, then placed.
///   • Background/Pos are in canvas pixels; Crop is in source pixels.
/// </summary>
public sealed class CollageComposer
{
    public sealed record CollageResult(
        bool Success, string? OutputPath, IReadOnlyList<string> UsedSourceFiles, int Placed, int Missing);

    /// <summary>
    /// Build the collage for one part. <paramref name="serialPrefixes"/> are the
    /// 12-char serial prefixes to search by (SZID and/or VirtualSerial).
    /// </summary>
    public CollageResult Compose(
        CollageLayout layout,
        IReadOnlyList<string> serialPrefixes,
        string sourceDir,
        string outputPath)
    {
        var candidates = FindCandidateFiles(serialPrefixes, sourceDir);

        using var canvas = new Bitmap(layout.CanvasWidth, layout.CanvasHeight, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(ParseColor(layout.BackgroundColor));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var placed = 0;
            var missing = 0;
            var used = new List<string>();

            foreach (var spec in layout.Images)
            {
                var file = MatchFile(candidates, spec.FileSuffix);
                if (file is null)
                {
                    missing++;
                    continue;
                }

                DrawImageSpec(g, spec, file);
                used.Add(file);
                placed++;
            }

            if (placed == 0)
                return new CollageResult(false, null, Array.Empty<string>(), 0, missing);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            canvas.Save(outputPath, FormatFor(outputPath));
            return new CollageResult(true, outputPath, used, placed, missing);
        }
    }

    private static void DrawImageSpec(Graphics g, CollageImageSpec spec, string file)
    {
        using var src = LoadUnlocked(file);

        // Crop rectangle (clamped to the source). Zero size = use the whole image.
        var crop = (spec.CropWidth > 0 && spec.CropHeight > 0)
            ? ClampRect(new Rectangle(spec.CropX, spec.CropY, spec.CropWidth, spec.CropHeight), src.Width, src.Height)
            : new Rectangle(0, 0, src.Width, src.Height);

        var factor = spec.Scale * spec.Zoom;
        var drawW = (float)(crop.Width * factor);
        var drawH = (float)(crop.Height * factor);

        var left = spec.PosX - drawW / 2f;
        var top = spec.PosY - drawH / 2f;

        // Destination parallelogram (UL, UR, LL). Swapping corners mirrors the image.
        var x0 = spec.MirrorX ? left + drawW : left;
        var x1 = spec.MirrorX ? left : left + drawW;
        var y0 = spec.MirrorY ? top + drawH : top;
        var y1 = spec.MirrorY ? top : top + drawH;

        var dest = new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x0, y1) };
        g.DrawImage(src, dest, crop, GraphicsUnit.Pixel);
    }

    /// <summary>
    /// Find all source files for this part by serial prefix. Files live in date
    /// subfolders, so we search recursively with an OS-level "prefix*" filter to
    /// avoid walking the whole NAS tree.
    /// </summary>
    private static List<string> FindCandidateFiles(IReadOnlyList<string> serialPrefixes, string sourceDir)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in serialPrefixes)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(sourceDir, prefix + "*", SearchOption.AllDirectories))
                {
                    if (seen.Add(file))
                        result.Add(file);
                }
            }
            catch
            {
                // Tree may be partially unavailable; skip silently (caller reports health).
            }
        }
        return result;
    }

    /// <summary>Pick the first candidate whose filename ends with the template's fixed suffix.</summary>
    private static string? MatchFile(List<string> candidates, string suffix)
    {
        if (string.IsNullOrEmpty(suffix))
            return null;

        foreach (var file in candidates)
        {
            if (Path.GetFileName(file).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }

    /// <summary>Load a bitmap detached from its file handle so the source can be deleted afterwards.</summary>
    private static Bitmap LoadUnlocked(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var img = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);
        return new Bitmap(img);
    }

    private static Rectangle ClampRect(Rectangle r, int maxW, int maxH)
    {
        var x = Math.Clamp(r.X, 0, Math.Max(0, maxW - 1));
        var y = Math.Clamp(r.Y, 0, Math.Max(0, maxH - 1));
        var w = Math.Clamp(r.Width, 1, maxW - x);
        var h = Math.Clamp(r.Height, 1, maxH - y);
        return new Rectangle(x, y, w, h);
    }

    private static ImageFormat FormatFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".bmp" => ImageFormat.Bmp,
            _ => ImageFormat.Png,
        };

    /// <summary>Parse a colour by name (White), hex (#RRGGBB) or "R,G,B". Falls back to White.</summary>
    private static Color ParseColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Color.White;

        value = value.Trim();

        if (value.StartsWith('#'))
        {
            if (int.TryParse(value[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
                return value.Length <= 7 ? Color.FromArgb(255, Color.FromArgb(argb)) : Color.FromArgb(argb);
        }

        if (value.Contains(','))
        {
            var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3
                && byte.TryParse(parts[0], out var r)
                && byte.TryParse(parts[1], out var gg)
                && byte.TryParse(parts[2], out var b))
                return Color.FromArgb(r, gg, b);
        }

        var named = Color.FromName(value);
        return named.IsKnownColor ? named : Color.White;
    }
}
