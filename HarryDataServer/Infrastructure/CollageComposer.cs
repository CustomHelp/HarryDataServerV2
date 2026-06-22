using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using HarryDataServer.Models;
using HarryDataServer.Services;

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
    // SOW §5.2.2 size-enforcement: re-encode JPEG from this quality downwards.
    private const long StartQuality = 85;
    private const long MinQuality = 30;
    private const long QualityStep = 5;

    private readonly ILogService _log;

    public CollageComposer(ILogService log) => _log = log;

    public sealed record CollageResult(
        bool Success, string? OutputPath, IReadOnlyList<string> UsedSourceFiles, int Placed, int Missing);

    /// <summary>
    /// Build the collage for one part. <paramref name="formattedSerials"/> are the
    /// serials with "_" inserted after char 12 (SZID and/or TrimmerSerial). Each image
    /// slot matches a *.bmp whose filename contains a serial AND all KeyName keywords.
    /// <paramref name="maxFileSizeKb"/> caps the JPEG output size (SOW §5.2.2); ≤ 0 disables the cap.
    /// </summary>
    public CollageResult Compose(
        CollageLayout layout,
        IReadOnlyList<string> formattedSerials,
        string sourceDir,
        string outputPath,
        int maxFileSizeKb)
    {
        var candidates = FindCandidateFiles(formattedSerials, sourceDir);

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
                var file = MatchByKeyName(candidates, spec.KeyName);
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
            Save(canvas, outputPath, maxFileSizeKb);
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
    /// Write the canvas to disk, enforcing the SOW §5.2.2 size cap. For JPEG output the
    /// image is re-encoded at iteratively lower quality (start 85, step −5, min 30) until
    /// it fits within <paramref name="maxFileSizeKb"/>. A WARNING is logged if the minimum
    /// quality is reached and the file still exceeds the limit. Non-JPEG formats (or a
    /// disabled cap) are written directly.
    /// </summary>
    private void Save(Bitmap canvas, string outputPath, int maxFileSizeKb)
    {
        var format = FormatFor(outputPath);
        var limitBytes = (long)maxFileSizeKb * 1024L;

        if (maxFileSizeKb <= 0 || format.Guid != ImageFormat.Jpeg.Guid)
        {
            canvas.Save(outputPath, format);
            if (maxFileSizeKb > 0 && new FileInfo(outputPath).Length > limitBytes)
                _log.Warning("Collage {Path} is {Kb} KB (> {Max} KB) but is not JPEG; size cap cannot be enforced.",
                    outputPath, new FileInfo(outputPath).Length / 1024, maxFileSizeKb);
            return;
        }

        var jpegCodec = JpegCodec();
        var quality = StartQuality;
        byte[] encoded;
        while (true)
        {
            encoded = EncodeJpeg(canvas, jpegCodec, quality);
            if (encoded.LongLength <= limitBytes || quality <= MinQuality)
                break;
            quality -= QualityStep;
        }

        File.WriteAllBytes(outputPath, encoded);

        if (encoded.LongLength > limitBytes)
            _log.Warning("Collage {Path}: {Kb} KB still exceeds the {Max} KB limit at minimum JPEG quality {Quality}.",
                outputPath, encoded.LongLength / 1024, maxFileSizeKb, quality);
        else if (quality < StartQuality)
            _log.Debug("Collage {Path} re-encoded at JPEG quality {Quality} to fit {Max} KB ({Kb} KB).",
                outputPath, quality, maxFileSizeKb, encoded.LongLength / 1024);
    }

    private static byte[] EncodeJpeg(Bitmap canvas, ImageCodecInfo codec, long quality)
    {
        using var ms = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        canvas.Save(ms, codec, parameters);
        return ms.ToArray();
    }

    private static ImageCodecInfo JpegCodec() =>
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// Find all *.bmp under the source whose filename contains one of the formatted
    /// serials (serial with "_" after char 12). Searched recursively.
    /// </summary>
    private static List<string> FindCandidateFiles(IReadOnlyList<string> formattedSerials, string sourceDir)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return result;

        var serials = formattedSerials.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (serials.Count == 0)
            return result;

        try
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*.bmp", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (serials.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    result.Add(file);
            }
        }
        catch
        {
            // Tree may be partially unavailable; caller reports health.
        }
        return result;
    }

    /// <summary>
    /// Pick the first candidate whose filename contains ALL keywords of the KeyName
    /// (V1 matching; KeyName split on whitespace). Returns null if KeyName is empty.
    /// </summary>
    private static string? MatchByKeyName(List<string> candidates, string keyName)
    {
        var tokens = (keyName ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return null;

        foreach (var file in candidates)
        {
            var name = Path.GetFileName(file);
            if (tokens.All(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
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
