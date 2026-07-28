using HarryDataServer.Services;

namespace HarryDataServer.Infrastructure;

/// <summary>One active measurement definition as it comes out of <c>measurement_definitions</c>.</summary>
public sealed record CsvColumnSource(int DefinitionId, string Camera, string Variable);

/// <summary>
/// Column layout of the production CSV (CLAUDE.md §13): builds the two header rows and the
/// definition → column mapping, and <b>merges the parallel strands / redundant control windows into
/// shared columns</b> (2026-07-28).
///
/// <para><b>Why:</b> the two production strands are mutually exclusive per part (strand A = M10+M20,
/// strand B = M11+M21) and the two M50 ST110 control windows likewise, while their variable names are
/// identical. One column per controller therefore produced 706 measurement columns of which 292 pairs
/// were always half empty. Verified on the live file <c>280726_141335_1118.csv</c> (4 099 data rows,
/// 1 099 036 filled cells in merged columns): <b>not one row</b> had both sources of a pair filled, so
/// merging is lossless.</para>
///
/// <para><b>Merge groups</b> (a column is identified by merge group + variable name):</para>
/// <list type="bullet">
///   <item><c>M10_&lt;Station&gt;_&lt;KF&gt;</c> ↔ <c>M11_…</c> → <c>M1x_&lt;Station&gt;_&lt;KF&gt;</c></item>
///   <item><c>M20_&lt;Station&gt;_&lt;KF&gt;</c> ↔ <c>M21_…</c> → <c>M2x_&lt;Station&gt;_&lt;KF&gt;</c></item>
///   <item><c>M50_ST110_KF1</c> ↔ <c>M50_ST110_KF3</c> → <c>M50_ST110</c></item>
///   <item>every other controller keeps its own name unchanged</item>
/// </list>
///
/// <para><b>Nothing is dropped silently.</b> A variable that exists on only ONE side of a pair (none
/// today — all five pairs are exactly identical in the live DB) keeps <b>its own column under the
/// original controller name</b> and is reported in <see cref="Warnings"/> once, at header-build time.
/// Merging can therefore never hide a measurement that has no counterpart.</para>
/// </summary>
public sealed class CsvColumnLayout
{
    private CsvColumnLayout(
        IReadOnlyList<string> controllerHeaders,
        IReadOnlyList<string> variableHeaders,
        IReadOnlyDictionary<int, int> columnByDefinitionId,
        IReadOnlyDictionary<int, int> valueColumnByResultDefinitionId,
        IReadOnlyDictionary<int, string> cameraByDefinitionId,
        IReadOnlyList<string> warnings,
        int sourceCount,
        int mergedColumnCount)
    {
        ControllerHeaders = controllerHeaders;
        VariableHeaders = variableHeaders;
        ColumnByDefinitionId = columnByDefinitionId;
        ValueColumnByResultDefinitionId = valueColumnByResultDefinitionId;
        CameraByDefinitionId = cameraByDefinitionId;
        Warnings = warnings;
        SourceCount = sourceCount;
        MergedColumnCount = mergedColumnCount;
    }

    /// <summary>Header row 1 per measurement column: the merge group (or the plain controller name).</summary>
    public IReadOnlyList<string> ControllerHeaders { get; }

    /// <summary>Header row 2 per measurement column: the variable name.</summary>
    public IReadOnlyList<string> VariableHeaders { get; }

    /// <summary>Definition → measurement column index. Several definitions may share one column.</summary>
    public IReadOnlyDictionary<int, int> ColumnByDefinitionId { get; }

    /// <summary>R_ definition → the column of its V_ partner (paired per CAMERA, as before).</summary>
    public IReadOnlyDictionary<int, int> ValueColumnByResultDefinitionId { get; }

    /// <summary>Definition → the controller that owns it (needed for the conflict rule + M50St110Kf).</summary>
    public IReadOnlyDictionary<int, string> CameraByDefinitionId { get; }

    /// <summary>One-off problems found while building the layout (logged by the caller).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Number of active definitions = number of columns the old (unmerged) layout produced.</summary>
    public int SourceCount { get; }

    /// <summary>How many columns actually carry more than one source definition.</summary>
    public int MergedColumnCount { get; }

    /// <summary>Number of measurement columns in this layout.</summary>
    public int ColumnCount => VariableHeaders.Count;

    /// <summary>
    /// The merge group of a controller — the label that appears in header row 1. Controllers that share
    /// a group are mutually exclusive per part and are folded into the same columns.
    /// </summary>
    public static string MergeGroup(string camera)
    {
        if (string.IsNullOrEmpty(camera))
            return camera;

        // M50 ST110 inspects every part twice through two control windows (KF1 / KF3).
        if (camera.Equals("M50_ST110_KF1", StringComparison.OrdinalIgnoreCase) ||
            camera.Equals("M50_ST110_KF3", StringComparison.OrdinalIgnoreCase))
            return "M50_ST110";

        // Parallel strands: M10/M11 and M20/M21 — "M1x_…" / "M2x_…" keeps the station + camera suffix.
        if (camera.StartsWith("M10_", StringComparison.OrdinalIgnoreCase) ||
            camera.StartsWith("M11_", StringComparison.OrdinalIgnoreCase))
            return "M1x_" + camera[4..];
        if (camera.StartsWith("M20_", StringComparison.OrdinalIgnoreCase) ||
            camera.StartsWith("M21_", StringComparison.OrdinalIgnoreCase))
            return "M2x_" + camera[4..];

        return camera;
    }

    /// <summary>True when the controller belongs to a merge group with a partner controller.</summary>
    public static bool IsMerged(string camera) => !MergeGroup(camera).Equals(camera, StringComparison.Ordinal);

    /// <summary>
    /// Build the layout. <paramref name="sources"/> must arrive in the order the columns should appear
    /// (the caller orders by camera name + telegram place, as before), so the column order of the
    /// unmerged layout is preserved — merged partners simply fold onto the first occurrence.
    /// </summary>
    public static CsvColumnLayout Build(IEnumerable<CsvColumnSource> sources)
    {
        var list = sources.ToList();

        // Which (group, variable) combinations have a partner on the OTHER controller of the group?
        // Only those may share a column; a one-sided variable stays on its own controller (and warns).
        var controllersPerKey = new Dictionary<(string Group, string Variable), HashSet<string>>();
        foreach (var s in list)
        {
            var key = (MergeGroup(s.Camera), s.Variable);
            if (!controllersPerKey.TryGetValue(key, out var set))
                controllersPerKey[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(s.Camera);
        }

        var warnings = new List<string>();
        var columnByKey = new Dictionary<(string Header, string Variable), int>();
        var columnByDefinition = new Dictionary<int, int>();
        var cameraByDefinition = new Dictionary<int, string>();
        var controllerHeaders = new List<string>();
        var variableHeaders = new List<string>();
        var sourcesPerColumn = new List<int>();

        foreach (var s in list)
        {
            var group = MergeGroup(s.Camera);
            var merged = !group.Equals(s.Camera, StringComparison.Ordinal);
            var partnered = controllersPerKey[(group, s.Variable)].Count > 1;

            // A merge candidate WITHOUT a partner keeps its own column under the original controller
            // name — never folded into the shared one, so nobody can mistake it for the other strand.
            var header = merged && !partnered ? s.Camera : group;
            if (merged && !partnered)
                warnings.Add($"'{s.Variable}' exists only on {s.Camera}, not on its {group} partner — " +
                             $"it keeps its own column '{s.Camera}' instead of being merged");

            var key = (header, s.Variable);
            if (!columnByKey.TryGetValue(key, out var index))
            {
                index = variableHeaders.Count;
                columnByKey[key] = index;
                controllerHeaders.Add(header);
                variableHeaders.Add(s.Variable);
                sourcesPerColumn.Add(0);
            }

            columnByDefinition[s.DefinitionId] = index;
            cameraByDefinition[s.DefinitionId] = s.Camera;
            sourcesPerColumn[index]++;
        }

        // R_/V_ pairing stays PER CAMERA (an M10 R_ pairs with the M10 V_); only the resulting column
        // index may be a shared one. Keying the pair on the merged label would be ambiguous.
        var valueColumnByResult = new Dictionary<int, int>();
        foreach (var group in list.GroupBy(s => (s.Camera, Base: MeasurementRowBuilder.StripTypePrefix(s.Variable))))
        {
            int? resultId = null;
            int? valueColumn = null;
            foreach (var s in group)
            {
                if (s.Variable.StartsWith("R_", StringComparison.Ordinal))
                    resultId = s.DefinitionId;
                else if (s.Variable.StartsWith("V_", StringComparison.Ordinal))
                    valueColumn = columnByDefinition[s.DefinitionId];
            }
            if (resultId.HasValue && valueColumn.HasValue)
                valueColumnByResult[resultId.Value] = valueColumn.Value;
        }

        return new CsvColumnLayout(
            controllerHeaders, variableHeaders, columnByDefinition, valueColumnByResult,
            cameraByDefinition, warnings, list.Count, sourcesPerColumn.Count(n => n > 1));
    }
}
