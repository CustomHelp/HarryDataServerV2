namespace HarryDataServer.Models;

/// <summary>
/// One image placement in a collage, read from an [ImageN] section of Collage.ini
/// (CLAUDE.md section 12). All geometry is in source/canvas pixels.
/// </summary>
public sealed class CollageImageSpec
{
    /// <summary>
    /// Filename template with the literal token <c>&lt;serial_pattern&gt;</c> standing in
    /// for the per-part serial prefix, e.g. <c>&lt;serial_pattern&gt;_M50_ST120_KF1_1_&amp;Cam1Img.bmp</c>.
    /// </summary>
    public string TemplateName { get; init; } = string.Empty;

    /// <summary>Canvas position of the image <b>centre</b> (assumption — see CollageComposer).</summary>
    public int PosX { get; init; }
    public int PosY { get; init; }

    /// <summary>Base resize factor applied to the cropped image.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>Additional magnification factor combined with <see cref="Scale"/>.</summary>
    public double Zoom { get; init; } = 1.0;

    /// <summary>Crop rectangle taken from the source image before scaling. Zero size = use whole image.</summary>
    public int CropX { get; init; }
    public int CropY { get; init; }
    public int CropWidth { get; init; }
    public int CropHeight { get; init; }

    public bool MirrorX { get; init; }
    public bool MirrorY { get; init; }

    /// <summary>Free-text key from Collage.ini (typically the controller name); informational.</summary>
    public string KeyName { get; init; } = string.Empty;

    /// <summary>The fixed suffix that real filenames must end with (TemplateName minus the token).</summary>
    public string FileSuffix => SerialPatternToken.Length == 0
        ? TemplateName
        : TemplateName[(TemplateName.IndexOf(SerialPatternToken, StringComparison.OrdinalIgnoreCase) is var i && i >= 0
            ? i + SerialPatternToken.Length
            : 0)..];

    /// <summary>The literal placeholder used in TemplateName.</summary>
    public const string SerialPatternToken = "<serial_pattern>";
}

/// <summary>
/// Parsed Collage.ini: canvas settings plus the ordered list of image placements.
/// Images are drawn in list order, so later entries appear on top.
/// </summary>
public sealed class CollageLayout
{
    public int CanvasWidth { get; init; } = 320;
    public int CanvasHeight { get; init; } = 650;
    public string BackgroundColor { get; init; } = "White";

    public IReadOnlyList<CollageImageSpec> Images { get; init; } = Array.Empty<CollageImageSpec>();

    public bool IsValid => CanvasWidth > 0 && CanvasHeight > 0 && Images.Count > 0;
}
