using System.IO;
using System.Text.RegularExpressions;

namespace HarryDataServer.Infrastructure;

/// <summary>How well a filename matched the camera's documented output format.</summary>
public enum ImageNameForm
{
    /// <summary>Spec form: Serial1 = 22 chars, Serial2 = 32 chars.</summary>
    Standard,

    /// <summary>
    /// Spec form with the two serial FIELD WIDTHS swapped (Serial1 = 32, Serial2 = 22). Written by the
    /// <b>M20/M21 camera 1</b> programs on the live line (verified 2026-07-28 over ~2 000 files); the
    /// content is unchanged (trimmer serial in Serial1, zeros in Serial2), only the field widths differ.
    /// </summary>
    SwappedWidths,

    /// <summary>
    /// The old V1 layout, still written by <b>M50_ST040_KF1</b> for its OCR images (~500/day into the
    /// NG folder, verified 2026-07-28): underscore separators, an extra <c>OCR</c> marker and the
    /// serial itself split by a <c>_</c> after character 12
    /// (<c>270726161219_00320440000000000000_1_M50_ST040_KF1_2_OCR_&amp;Cam2Img_Dark.png</c>).
    /// <see cref="ImageFileName.Serial1"/> is reassembled without the underscore, so it is identical to
    /// the modern form's Serial1 for the same part and matches with the same search key.
    /// </summary>
    LegacyUnderscore,

    /// <summary>Parsed, but the field widths match neither the spec nor the known swap — treat with care.</summary>
    OffSpec,
}

/// <summary>
/// THE single parser for Keyence image filenames (CLAUDE.md §11). Nothing else may split an image
/// filename — every search/move/delete decision goes through here.
///
/// <para><b>Binding specification (Philipp, 2026-07-28 — this is how the camera writes it):</b></para>
/// <code>
/// {Serial1}-{Serial2}-{Overall}-{Controller}-{CameraNumber}-{ImageVariable}.bmp
/// </code>
/// <list type="bullet">
///   <item><b>Serial1</b> — always exactly 22 chars, right-padded with '0'.</item>
///   <item><b>Serial2</b> — always exactly 32 chars, right-padded with '0'.</item>
///   <item><b>Overall</b> — camera's overall result, <c>0</c> or <c>1</c>.</item>
///   <item><b>Controller</b> — e.g. <c>M50_ST140_KF1</c>; <b>contains underscores</b>.</item>
///   <item><b>CameraNumber</b> — 1–4 on M1X, 1–2 elsewhere, always 1 on M50 ST110.</item>
///   <item><b>ImageVariable</b> — e.g. <c>&amp;Cam1Img</c>, <c>&amp;Cam1Img_Dark</c>; contains
///     <c>&amp;</c>, underscores and dots.</item>
/// </list>
///
/// <para><b>Field content per operating mode / module:</b></para>
/// <list type="bullet">
///   <item>Normal M1X + M50 — Serial1 = frame serial (19 digits + '0' padding), Serial2 = all zeros.</item>
///   <item>Normal M2X — Serial1 = virtual (trimmer) serial, Serial2 = all zeros.</item>
///   <item>MSA — Serial1 = full BaseID incl. loop counter, Serial2 = <b>the DMC lasered on the part</b>
///     (real content, NOT a zero field). This is what <see cref="IsMsa"/> keys on.</item>
/// </list>
///
/// <para><b>Why offsets and not <c>Split('-')</c>:</b> the controller name and the image variable
/// contain underscores (harmless) but a DMC may in theory contain a hyphen, and the trailing fields
/// are positional. Serial1/Serial2 are therefore separated at the FIXED offset (separator expected at
/// index 22, or 32 for the known M2X-camera-1 swap) and the trailing three fields are taken from the
/// end, so a hyphen inside the DMC cannot shift any field.</para>
///
/// <para><b>Underscore history:</b> the early V1 camera wrote the serial with a <c>_</c> after
/// character 12 and used <c>_</c> as the field separator. Since the change to the two serial fields
/// there is no underscore in the serial. One camera program was never migrated
/// (<see cref="ImageNameForm.LegacyUnderscore"/>), so both forms are parsed here.</para>
/// </summary>
public sealed record ImageFileName(
    string Serial1, string Serial2, string Overall, string Controller, string CameraNumber,
    string ImageName, ImageNameForm Form)
{
    /// <summary>Spec width of Serial1.</summary>
    public const int Serial1Width = 22;

    /// <summary>Spec width of Serial2.</summary>
    public const int Serial2Width = 32;

    // Controller names are always M<dd>_ST<ddd>_KF<d> — used to locate the tail of a legacy name.
    private static readonly Regex LegacyPattern = new(
        @"^(?<s1a>\d+)_(?<s1b>\d+)_(?<ov>[01])_(?<ctrl>M\d{2}_ST\d{3}_KF\d+)_(?<cam>\d+)(?<extra>(?:_[^&_]+)*)_(?<img>&.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when this image belongs to an MSA / LimitSample run. The ONLY reliable marker is Serial2:
    /// in Normal mode the camera fills it with zeros, in MSA mode it carries the real DMC of the test
    /// part. Production sweeps (DE purge, OK backup/delete, Input-leftover retention, collage source
    /// search) must skip these — MSA images are QS evidence and are never aged out by production rules.
    /// </summary>
    public bool IsMsa => Serial2.Length > 0 && Serial2.Any(c => c != '0');

    /// <summary>The 12-char search key (NG ↔ low-res retention linkage): first 12 chars of Serial1.</summary>
    public string Serial12 => Serial1.Length >= 12 ? Serial1[..12] : Serial1;

    /// <summary>The 14-char BaseID (MSA mode): first 14 chars of Serial1.</summary>
    public string BaseId14 => Serial1.Length >= 14 ? Serial1[..14] : Serial1;

    /// <summary>
    /// Production match: the given serial (frame 19 / trimmer 13 / virtual serial, normalised via
    /// <see cref="SerialNumberHelper.ToImageSearchKey"/>) is a PREFIX OF SERIAL1 — never a substring of
    /// the whole filename. A substring test would also hit the DMC in Serial2, the controller name or
    /// the image variable (relevant in MSA mode, where Serial2 carries real content).
    /// </summary>
    public bool MatchesSerial1Prefix(string serialKey) =>
        serialKey.Length > 0 && Serial1.StartsWith(serialKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>MSA match: the run's 14-char BaseID is a prefix of Serial1 (loop counter + padding follow).</summary>
    public bool MatchesBaseIdField(string baseId14) =>
        baseId14.Length > 0 && Serial1.StartsWith(baseId14, StringComparison.Ordinal);

    /// <summary>MSA match: the test part's DMC is the content of Serial2 (padding may follow).</summary>
    public bool MatchesDmcField(string dmc) =>
        dmc.Length > 0 && Serial2.StartsWith(dmc, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Serial1 of a filename, or null when the name cannot be parsed.</summary>
    public static string? Serial1Of(string fileName) => TryParse(fileName)?.Serial1;

    /// <summary>
    /// True when the image belongs to the given (14-char) BaseID. Unparsable names never match.
    /// </summary>
    public static bool MatchesBaseId(string fileName, string baseId14) =>
        TryParse(fileName) is { } parsed && parsed.MatchesBaseIdField(baseId14);

    /// <summary>True when Serial1 starts with the given serial prefix. Unparsable names never match.</summary>
    public static bool MatchesSerialPrefix(string fileName, string serialPrefix) =>
        TryParse(fileName) is { } parsed && parsed.MatchesSerial1Prefix(serialPrefix);

    /// <summary>
    /// Parse a filename into its six fields. Returns <c>null</c> when the name matches neither the
    /// modern nor the legacy layout — callers must treat that as "unknown" and, for anything
    /// destructive, leave the file alone and report it (see <see cref="ImageNameForm"/>).
    /// </summary>
    public static ImageFileName? TryParse(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        return fileName.Contains('-', StringComparison.Ordinal)
            ? TryParseModern(fileName)
            : TryParseLegacy(fileName);
    }

    // ---- modern form: {Serial1 22}-{Serial2 32}-{overall}-{controller}-{cam}-&{image} ------------

    private static ImageFileName? TryParseModern(string fileName)
    {
        var amp = fileName.IndexOf('&');
        if (amp <= 0)
            return null;

        var imageName = fileName[amp..];
        var head = fileName[..amp].TrimEnd('-');

        // The three trailing fields are positional and contain no hyphen; taking them from the END
        // keeps a hyphen inside the DMC (Serial2) from shifting anything.
        var p3 = head.LastIndexOf('-');
        if (p3 <= 0) return null;
        var p2 = head.LastIndexOf('-', p3 - 1);
        if (p2 <= 0) return null;
        var p1 = head.LastIndexOf('-', p2 - 1);
        if (p1 <= 0) return null;

        var cameraNumber = head[(p3 + 1)..];
        var controller = head[(p2 + 1)..p3];
        var overall = head[(p1 + 1)..p2];
        var serials = head[..p1];

        // Split the two serial fields at their FIXED offset, not at "the first hyphen": the separator
        // must sit at index 22 (spec) or 32 (the known M20/M21 camera-1 swap).
        int separator;
        ImageNameForm form;
        if (serials.Length > Serial1Width && serials[Serial1Width] == '-')
        {
            separator = Serial1Width;
            form = ImageNameForm.Standard;
        }
        else if (serials.Length > Serial2Width && serials[Serial2Width] == '-')
        {
            separator = Serial2Width;
            form = ImageNameForm.SwappedWidths;
        }
        else
        {
            // Off-spec width: still parse (first hyphen) so the file can be identified, but mark it
            // so destructive callers can decide to leave it alone / report it.
            separator = serials.IndexOf('-');
            if (separator <= 0)
                return null;
            form = ImageNameForm.OffSpec;
        }

        var serial1 = serials[..separator];
        var serial2 = serials[(separator + 1)..];

        // Cross-check the trailing width too; a wrong Serial2 width would break the MSA detection.
        if (form == ImageNameForm.Standard && serial2.Length != Serial2Width)
            form = ImageNameForm.OffSpec;
        else if (form == ImageNameForm.SwappedWidths && serial2.Length != Serial1Width)
            form = ImageNameForm.OffSpec;

        return new ImageFileName(serial1, serial2, overall, controller, cameraNumber, imageName, form);
    }

    // ---- legacy form: {s1 12}_{s1rest}_{overall}_{controller}_{cam}[_{extra}]_&{image} -----------

    private static ImageFileName? TryParseLegacy(string fileName)
    {
        var m = LegacyPattern.Match(fileName);
        if (!m.Success)
            return null;

        // Reassemble the serial WITHOUT the historic '_' after char 12, so it is byte-identical to the
        // modern form's Serial1 for the same part and matches with the very same search key.
        var serial1 = m.Groups["s1a"].Value + m.Groups["s1b"].Value;

        return new ImageFileName(
            serial1,
            string.Empty,                       // the legacy form has no second serial field
            m.Groups["ov"].Value,
            m.Groups["ctrl"].Value,
            m.Groups["cam"].Value,
            m.Groups["img"].Value,
            ImageNameForm.LegacyUnderscore);
    }
}
