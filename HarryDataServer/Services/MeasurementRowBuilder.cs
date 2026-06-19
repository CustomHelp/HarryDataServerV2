using HarryDataServer.Models;

namespace HarryDataServer.Services;

/// <summary>
/// Combines the alternating R_ (result) and V_ (value) entries of a "Results"
/// telegram into one DB row per measurement pair (CLAUDE.md section 4): the row is
/// keyed by the R_ definition and carries both <c>result_status</c> (from R_) and
/// <c>measurement_value</c> (from V_). R_/V_ entries are paired by their shared base
/// name (the variable name without the "R_"/"V_" prefix).
/// </summary>
public static class MeasurementRowBuilder
{
    public static List<PendingMeasurement> Build(
        string cameraName,
        string serial,
        bool isTrimmer,
        byte runType,
        DateTime measuredAt,
        IReadOnlyList<MeasurementSample> samples)
    {
        var rows = new List<PendingMeasurement>();

        // Index the value (V_) entries by base name so each result can find its partner.
        var valueByBase = new Dictionary<string, MeasurementSample>(StringComparer.Ordinal);
        var results = new List<MeasurementSample>();
        foreach (var sample in samples)
        {
            if (sample.IsResult)
                results.Add(sample);
            else
                valueByBase[StripTypePrefix(sample.VariableName)] = sample;
        }

        var pairedBases = new HashSet<string>(StringComparer.Ordinal);

        // One combined row per R_ entry (keyed by the R_ definition).
        foreach (var result in results)
        {
            var baseName = StripTypePrefix(result.VariableName);
            valueByBase.TryGetValue(baseName, out var value);
            if (value is not null)
                pairedBases.Add(baseName);

            rows.Add(new PendingMeasurement
            {
                CameraName = cameraName,
                VariableName = result.VariableName,   // R_ → resolves to the R_ definition_id
                Serial = serial,
                IsTrimmer = isTrimmer,
                ResultStatus = result.ResultStatus,
                Value = value?.Value,
                MeasurementString = value is { Value: null } ? value.RawField : null,
                RunType = runType,
                MeasuredAt = measuredAt,
            });
        }

        // Value entries without a matching R_ partner are stored on their own.
        foreach (var (baseName, value) in valueByBase)
        {
            if (pairedBases.Contains(baseName))
                continue;

            rows.Add(new PendingMeasurement
            {
                CameraName = cameraName,
                VariableName = value.VariableName,
                Serial = serial,
                IsTrimmer = isTrimmer,
                Value = value.Value,
                MeasurementString = value.Value is null ? value.RawField : null,
                RunType = runType,
                MeasuredAt = measuredAt,
            });
        }

        return rows;
    }

    /// <summary>Remove a leading "R_"/"V_" prefix to get the shared feature base name.</summary>
    public static string StripTypePrefix(string variableName)
    {
        if (variableName.Length > 2 &&
            (variableName.StartsWith("R_", StringComparison.Ordinal) ||
             variableName.StartsWith("V_", StringComparison.Ordinal)))
        {
            return variableName.Substring(2);
        }
        return variableName;
    }
}
