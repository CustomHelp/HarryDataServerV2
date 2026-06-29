using System.IO;

namespace HarryDataServer.Infrastructure;

/// <summary>
/// Parser for the Keyence image filename format (CLAUDE.md §11). The same camera program
/// runs in Normal and MSA mode, so the structure is identical — only the first two fields
/// differ. The field separator is the hyphen <c>-</c> (the SZID itself contains an
/// underscore after char 12, so underscore cannot be the separator):
/// <code>
/// &lt;Field1 22&gt;-&lt;Field2 32&gt;-&lt;overall 1|0&gt;-&lt;Controller&gt;-&lt;Nest&gt;-&amp;&lt;ImageName&gt;.ext
/// </code>
/// • Field 1 (≤22 chars): Normal = SZID (frame serial) · MSA = BaseID(14)+Loop(3)+padding.
///   Capped to 22 chars (<see cref="SerialField.MaxLength"/>) on parse to match the stored
///   Serial1 (CLAUDE.md §4) and stay robust against any longer legacy filename.
/// • Field 2 (32 chars): Normal = all zeros (ignore) · MSA = DMC printed on the test part.
///   The DMC may itself contain hyphens, so Field 2 is recovered by anchoring on the
///   <c>&amp;ImageName</c> tail and the fixed trailing fields rather than a naive split.
/// </summary>
public sealed record ImageFileName(
    string Field1, string Field2, string Overall, string Controller, string Nest, string ImageName)
{
    /// <summary>The 12-char image search key (CLAUDE.md §6/§11): first 12 chars of Field 1.</summary>
    public string Serial12 => Field1.Length >= 12 ? Field1[..12] : Field1;

    /// <summary>The 14-char BaseID (MSA mode): first 14 chars of Field 1.</summary>
    public string BaseId14 => Field1.Length >= 14 ? Field1[..14] : Field1;

    /// <summary>
    /// The NAS-sorted root of an image base path: the parent of a trailing <c>Input</c> segment
    /// (the NAS moves images out of <c>…\Input</c> into <c>…\YYYY\MM\DD</c> day-folders beside it),
    /// or the path itself when it does not end in <c>Input</c>. Null/empty in → null.
    /// </summary>
    public static string? SortedRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(trimmed), "Input", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(trimmed)
            : trimmed;
    }

    /// <summary>Field 1 of a filename: everything up to the first hyphen, capped to 22 chars
    /// (<see cref="SerialField.MaxLength"/>). Field 1 (SZID or BaseID+loop+padding) never
    /// contains a hyphen, so the split is unambiguous; the cap matches the stored Serial1.</summary>
    public static string? Field1Of(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;
        var dash = fileName.IndexOf('-');
        if (dash <= 0)
            return null;
        return SerialField.Cap(fileName[..dash]);
    }

    /// <summary>
    /// True when the image belongs to the given (14-char) BaseID, i.e. Field 1 starts with it
    /// (the loop counter and zero padding follow). Used to gather one MSA run's images.
    /// </summary>
    public static bool MatchesBaseId(string fileName, string baseId14)
    {
        if (string.IsNullOrEmpty(baseId14))
            return false;
        var field1 = Field1Of(fileName);
        return field1 is not null && field1.StartsWith(baseId14, StringComparison.Ordinal);
    }

    /// <summary>True when Field 1 starts with the 12-char serial prefix (Normal-mode match).</summary>
    public static bool MatchesSerialPrefix(string fileName, string serial12)
    {
        if (string.IsNullOrEmpty(serial12))
            return false;
        var field1 = Field1Of(fileName);
        return field1 is not null && field1.StartsWith(serial12, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fully parse a filename into its fields. Returns null if it does not match the format.
    /// Field 2 is recovered by anchoring on the <c>&amp;ImageName</c> tail, so a DMC that
    /// itself contains hyphens is preserved intact.
    /// </summary>
    public static ImageFileName? TryParse(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        var dash = fileName.IndexOf('-');
        if (dash <= 0)
            return null;

        var field1 = SerialField.Cap(fileName[..dash]); // only the first 22 chars are meaningful (§4)
        var rest = fileName[(dash + 1)..]; // <Field2>-<overall>-<controller>-<nest>-&<imageName>.ext

        var amp = rest.IndexOf('&');
        if (amp < 0)
            return null;

        var imageName = rest[amp..];               // &Cam1Img.Height.png
        var head = rest[..amp].TrimEnd('-');       // <Field2>-<overall>-<controller>-<nest>

        var segs = head.Split('-');
        if (segs.Length < 4)
            return null;

        var nest = segs[^1];
        var controller = segs[^2];
        var overall = segs[^3];
        var field2 = string.Join("-", segs[..^3]); // DMC may contain hyphens

        return new ImageFileName(field1, field2, overall, controller, nest, imageName);
    }
}
