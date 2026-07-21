namespace HarryShared.Data;

/// <summary>
/// Baugleich (mirror) module pairs: strand 1 and strand 2 are physically identical machines, so
/// everything that runs on one also runs on the other. M10 ≡ M11 and M20 ≡ M21 therefore SHARE their
/// MSA1 and LimitSample references — a reference taught for M10 must also apply to M11 and vice versa.
/// M50 (and anything not listed) has no mirror. Single source of truth: the reference is stored once
/// under the module it was taught on; readers resolve the pair via <see cref="Group"/> / <see cref="MirrorOf"/>.
/// </summary>
public static class ModuleMirror
{
    private static readonly Dictionary<string, string> Pairs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["M10"] = "M11", ["M11"] = "M10",
        ["M20"] = "M21", ["M21"] = "M20",
    };

    /// <summary>The baugleich partner module, or null when the module has no mirror.</summary>
    public static string? MirrorOf(string? module) =>
        !string.IsNullOrEmpty(module) && Pairs.TryGetValue(module, out var m) ? m : null;

    /// <summary>The module itself plus its mirror (if any), own module first (used for reference lookup).</summary>
    public static IReadOnlyList<string> Group(string module)
    {
        var mirror = MirrorOf(module);
        return mirror is null ? new[] { module } : new[] { module, mirror };
    }
}
