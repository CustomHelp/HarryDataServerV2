namespace HarryDataServer.Infrastructure;

/// <summary>
/// Single normalisation point for a Serial1 frame/trimmer serial (SZID / Virtual Serial).
///
/// The same serial reaches us on two different wires and must end up identical in the DB so the
/// part-exit measurement lookup (<c>measurements_serial</c>/<c>measurements_serial_trimmer</c> ↔
/// <c>dmcserial</c>) succeeds:
///
///  • Camera telegram — the Keyence controller emits Serial1 as 32 single-char tokens, right-padded
///    with '0' to the field width. <see cref="Communication.TelegramParser"/> concatenates and
///    truncates them to <see cref="SerialField.MaxLength"/> (22), so the stored value keeps the
///    controller's trailing padding, e.g. <c>2007261628430024167</c> + <c>000</c>.
///  • SPS part-exit telegram — the PLC delivers the same serial UNPADDED (its true length), e.g.
///    <c>2007261628430024167</c>.
///
/// Without normalisation the two never compare equal (the measurement rows carry extra trailing
/// zeros), so the Excel/CSV export at part exit finds no measurements for the part (Problem 1).
///
/// <para>
/// Normalisation strips ONLY the controller's right-padding: everything past the configured
/// <see cref="MeaningfulLength"/> is dropped **only when it is all '0'** (i.e. genuine padding).
/// A serial whose tail past the meaningful length is non-zero is left untouched — this is
/// deliberately NOT a blind <c>TrimEnd('0')</c>, so a real serial that legitimately ends in '0'
/// (within the meaningful length) is preserved.
/// </para>
///
/// <para>
/// The meaningful length differs by serial KIND (confirmed on the live line 2026-07-27):
/// the <b>frame</b> serial (SZID, M1X/M5X) is <see cref="DefaultMeaningfulLength"/> (19); the
/// <b>trimmer</b> serial (Virtual Serial, M20/M21) is <see cref="DefaultTrimmerLength"/> (13). They
/// are overridable via <c>[General] SerialNumberLength</c> / <c>[General] TrimmerSerialNumberLength</c>.
/// Use <see cref="Normalize"/> for frame serials and <see cref="NormalizeTrimmer"/> for trimmer
/// serials. The DMC (Serial2) is a different, wider field and is intentionally NOT normalised here.
/// </para>
///
/// <para>
/// Why two lengths: the camera pads the trimmer serial with trailing '0' to the field width, so a
/// trimmer telegram stored via the camera pipeline kept 6 extra zeros (13 + 6 = 19) while the SPS
/// delivered the same serial at its true 13 chars — the two never compared equal, so the part-exit
/// lookup fell back to a prefix match (a WARNING) and any trimmer image search missed. Normalising
/// the trimmer to 13 on both wires fixes this at the root.
/// </para>
/// </summary>
public static class SerialNumberHelper
{
    /// <summary>Default meaningful (unpadded) length of a FRAME Serial1 serial — confirmed 19 on the live line.</summary>
    public const int DefaultMeaningfulLength = 19;

    /// <summary>Default meaningful (unpadded) length of a TRIMMER serial (M20/M21) — confirmed 13 on the live line.</summary>
    public const int DefaultTrimmerLength = 13;

    private static int _meaningfulLength = DefaultMeaningfulLength;
    private static int _trimmerLength = DefaultTrimmerLength;

    /// <summary>The configured meaningful (unpadded) FRAME serial length used for padding removal.</summary>
    public static int MeaningfulLength => _meaningfulLength;

    /// <summary>The configured meaningful (unpadded) TRIMMER serial length used for padding removal.</summary>
    public static int TrimmerLength => _trimmerLength;

    /// <summary>
    /// Set the meaningful FRAME serial length once at startup (from <c>[General] SerialNumberLength</c>).
    /// Ignored for out-of-range values (must be 1..<see cref="SerialField.MaxLength"/>), so a bad
    /// INI value can never disable normalisation or exceed the DB column width.
    /// </summary>
    public static void Configure(int meaningfulLength)
    {
        if (meaningfulLength >= 1 && meaningfulLength <= SerialField.MaxLength)
            _meaningfulLength = meaningfulLength;
    }

    /// <summary>
    /// Set the meaningful TRIMMER serial length once at startup (from
    /// <c>[General] TrimmerSerialNumberLength</c>). Same guard as <see cref="Configure"/>.
    /// </summary>
    public static void ConfigureTrimmer(int trimmerLength)
    {
        if (trimmerLength >= 1 && trimmerLength <= SerialField.MaxLength)
            _trimmerLength = trimmerLength;
    }

    /// <summary>
    /// Canonicalise a FRAME Serial1 value: trim, drop the controller's trailing '0' padding beyond
    /// <see cref="MeaningfulLength"/>, and cap to <see cref="SerialField.MaxLength"/>. Null/empty
    /// returns <see cref="string.Empty"/>. Idempotent — normalising an already-normalised value is
    /// a no-op.
    /// </summary>
    public static string Normalize(string? value) => NormalizeTo(value, _meaningfulLength);

    /// <summary>
    /// Canonicalise a TRIMMER serial (M20/M21 Virtual Serial) using <see cref="TrimmerLength"/>.
    /// Same padding-only rule as <see cref="Normalize"/>. Composable with the frame normalisation
    /// the parser already applied (e.g. 13 + 6 zeros → 13), since it only ever trims trailing '0'.
    /// </summary>
    public static string NormalizeTrimmer(string? value) => NormalizeTo(value, _trimmerLength);

    /// <summary>
    /// Sanitise a serial into an <b>image-filename search key</b> (the Field 1 prefix used by every
    /// image search: collage source lookup, OK-part cleanup, DE purge).
    ///
    /// <para>
    /// Field 1 of a real filename is the serial right-padded with <c>'0'</c> and contains
    /// <b>no separators at all</b> (confirmed on the live line 2026-07-28, e.g.
    /// <c>2707261603210031811000-000…0-1-M50_ST140_KF1-1-&amp;Cam1Img.bmp</c>). So the only correct
    /// key is the bare serial. This drops every character that cannot occur in Field 1 —
    /// whitespace and separators such as <c>'_'</c> / <c>'-'</c> — and caps to
    /// <see cref="SerialField.MaxLength"/>.
    /// </para>
    ///
    /// <para>
    /// It deliberately does <b>not</b> trim trailing <c>'0'</c>: the caller passes a serial that is
    /// already normalised to its KIND length (frame 19 via <see cref="Normalize"/>, trimmer 13 via
    /// <see cref="NormalizeTrimmer"/>), and shortening it further would let the prefix spill onto an
    /// adjacent serial that differs only in a late character.
    /// </para>
    /// </summary>
    public static string ToImageSearchKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var buffer = new char[Math.Min(value.Length, SerialField.MaxLength)];
        var length = 0;
        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c))
                continue;
            if (length == buffer.Length)
                break;
            buffer[length++] = c;
        }
        return new string(buffer, 0, length);
    }

    private static string NormalizeTo(string? value, int meaningfulLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var s = value.Trim();

        // Remove padding only when the tail past the meaningful length is entirely '0' (padding).
        // A non-zero tail means a genuinely longer serial — keep it (never blindly trim zeros).
        if (s.Length > meaningfulLength && IsAllZero(s, meaningfulLength))
            s = s[..meaningfulLength];

        // Never exceed the VARCHAR(22) serial columns.
        return s.Length > SerialField.MaxLength ? s[..SerialField.MaxLength] : s;
    }

    private static bool IsAllZero(string s, int start)
    {
        for (var i = start; i < s.Length; i++)
            if (s[i] != '0')
                return false;
        return true;
    }
}
