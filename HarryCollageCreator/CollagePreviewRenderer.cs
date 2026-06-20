using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;

namespace HarryCollageCreator;

/// <summary>
/// Renders the live collage preview using the <b>same GDI+ semantics</b> as the
/// server's CollageComposer so the authored layout matches the runtime output:
///   • Pos_X/Pos_Y address the image centre on the canvas.
///   • Effective draw size = crop size × Scale × Zoom.
///   • Order: crop → scale → mirror → place.
/// The selected slot is outlined to aid placement.
/// </summary>
public static class CollagePreviewRenderer
{
    public static BitmapSource Render(
        int canvasWidth, int canvasHeight, string backgroundColor,
        IReadOnlyList<ImageSlot> slots, int selectedIndex)
    {
        canvasWidth = Math.Clamp(canvasWidth, 1, 8000);
        canvasHeight = Math.Clamp(canvasHeight, 1, 8000);

        using var canvas = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(ParseColor(backgroundColor));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (string.IsNullOrEmpty(slot.SourceFilePath) || !File.Exists(slot.SourceFilePath))
                    continue;

                try { DrawSlot(g, slot, i == selectedIndex); }
                catch { /* skip an unreadable sample image in the preview */ }
            }
        }

        return ToBitmapSource(canvas);
    }

    private static void DrawSlot(Graphics g, ImageSlot slot, bool selected)
    {
        using var src = LoadUnlocked(slot.SourceFilePath);

        var crop = (slot.CropWidth > 0 && slot.CropHeight > 0)
            ? ClampRect(new Rectangle(slot.CropX, slot.CropY, slot.CropWidth, slot.CropHeight), src.Width, src.Height)
            : new Rectangle(0, 0, src.Width, src.Height);

        var factor = slot.Scale * slot.Zoom;
        var drawW = (float)(crop.Width * factor);
        var drawH = (float)(crop.Height * factor);

        var left = slot.PosX - drawW / 2f;
        var top = slot.PosY - drawH / 2f;

        var x0 = slot.MirrorX ? left + drawW : left;
        var x1 = slot.MirrorX ? left : left + drawW;
        var y0 = slot.MirrorY ? top + drawH : top;
        var y1 = slot.MirrorY ? top : top + drawH;

        var dest = new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x0, y1) };
        g.DrawImage(src, dest, crop, GraphicsUnit.Pixel);

        if (selected)
        {
            using var pen = new Pen(Color.FromArgb(0x8B, 0x5C, 0xF6), 2f);
            g.DrawRectangle(pen, left, top, drawW, drawH);
        }
    }

    private static Bitmap LoadUnlocked(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var img = Image.FromStream(fs, false, false);
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

    private static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Parse a colour by name (White), hex (#RRGGBB) or "R,G,B". Falls back to White.</summary>
    public static Color ParseColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Color.White;
        value = value.Trim();

        if (value.StartsWith('#') &&
            int.TryParse(value[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            return value.Length <= 7 ? Color.FromArgb(255, Color.FromArgb(argb)) : Color.FromArgb(argb);

        if (value.Contains(','))
        {
            var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && byte.TryParse(parts[0], out var r) &&
                byte.TryParse(parts[1], out var gg) && byte.TryParse(parts[2], out var b))
                return Color.FromArgb(r, gg, b);
        }

        var named = Color.FromName(value);
        return named.IsKnownColor ? named : Color.White;
    }
}
