namespace HarryDataServer.Models;

/// <summary>
/// The seven PLC/SPS channels (CLAUDE.md section 5). We are always the TCP server.
/// </summary>
public enum SpsChannel
{
    /// <summary>Channel 1 — mirror the telegram back plus the per-camera status string.</summary>
    KeepAlive,

    /// <summary>Channel 2 — Part Exit at St160 packaging (triggers CSV/Collage/MSA).</summary>
    PartExit,

    /// <summary>Channels 3–7 — MSA evaluation trigger per module.</summary>
    MsaM10,
    MsaM11,
    MsaM20,
    MsaM21,
    MsaM50,
}

public static class SpsChannelExtensions
{
    /// <summary>Module key ("M10".."M50") for the MSA channels; empty for the others.</summary>
    public static string ModuleKey(this SpsChannel channel) => channel switch
    {
        SpsChannel.MsaM10 => "M10",
        SpsChannel.MsaM11 => "M11",
        SpsChannel.MsaM20 => "M20",
        SpsChannel.MsaM21 => "M21",
        SpsChannel.MsaM50 => "M50",
        _ => string.Empty,
    };

    public static bool IsMsaChannel(this SpsChannel channel) =>
        channel is SpsChannel.MsaM10 or SpsChannel.MsaM11 or SpsChannel.MsaM20
            or SpsChannel.MsaM21 or SpsChannel.MsaM50;

    /// <summary>The MSA channel that serves a module key ("M10".."M50"), or null if unknown.
    /// Reverse of <see cref="ModuleKey"/> — used to push a completed MSA result back to the PLC.</summary>
    public static SpsChannel? MsaChannelForModule(string? moduleKey) => moduleKey?.Trim().ToUpperInvariant() switch
    {
        "M10" => SpsChannel.MsaM10,
        "M11" => SpsChannel.MsaM11,
        "M20" => SpsChannel.MsaM20,
        "M21" => SpsChannel.MsaM21,
        "M50" => SpsChannel.MsaM50,
        _ => null,
    };

    /// <summary>1-based channel number as documented in CLAUDE.md section 5.</summary>
    public static int Number(this SpsChannel channel) => (int)channel + 1;

    /// <summary>Human-readable channel description for the UI.</summary>
    public static string Description(this SpsChannel channel) => channel switch
    {
        SpsChannel.KeepAlive => "KeepAlive / Status",
        SpsChannel.PartExit => "Part Exit (St160)",
        SpsChannel.MsaM10 => "MSA Trigger M10",
        SpsChannel.MsaM11 => "MSA Trigger M11",
        SpsChannel.MsaM20 => "MSA Trigger M20",
        SpsChannel.MsaM21 => "MSA Trigger M21",
        SpsChannel.MsaM50 => "MSA Trigger M50",
        _ => channel.ToString(),
    };
}
