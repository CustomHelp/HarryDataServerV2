using CommunityToolkit.Mvvm.ComponentModel;
using HarryShared.Data;

namespace HarryLimitSample;

/// <summary>One taught part shown in the editor's "taught parts" list (per-part reference file).</summary>
public sealed record TaughtPartRow(string Dmc, DateTime TaughtAt, int ExpectedFailCount, string Module);

/// <summary>The operator's expectation for one measurement in a LimitSample reference.</summary>
public enum Expectation
{
    /// <summary>Not part of the reference (left out of the file).</summary>
    Ignore,
    /// <summary>Must be accepted (a good feature) — limit_sample_expected = false.</summary>
    ShouldPass,
    /// <summary>Prepared error that must be rejected — limit_sample_expected = true.</summary>
    ShouldFail,
}

/// <summary>One editable measurement row in the LimitSample editor.</summary>
public partial class LimitSampleRow : ObservableObject
{
    public LimitSampleRow(PartMeasurementRow source)
    {
        DisplayName = source.DisplayName;
        Module = source.Module;
        CameraName = source.CameraName;
        ValueText = source.ValueText;
        ResultText = source.ResultText;
        // Pre-populate the expectation from the measurement's actual result:
        //   1 (GOOD) → ShouldPass, 0 (BAD) → ShouldFail, anything else → Ignore.
        _expectation = source.ResultStatus switch
        {
            1 => Expectation.ShouldPass,
            0 => Expectation.ShouldFail,
            _ => Expectation.Ignore,
        };
    }

    /// <summary>Construct a row directly (used when loading an existing reference file).</summary>
    public LimitSampleRow(string displayName, string module, Expectation expectation)
    {
        DisplayName = displayName;
        Module = module;
        CameraName = string.Empty;
        ValueText = string.Empty;
        ResultText = string.Empty;
        _expectation = expectation;
    }

    public string DisplayName { get; }
    public string Module { get; }
    public string CameraName { get; }
    public string ValueText { get; }
    public string ResultText { get; }

    [ObservableProperty] private Expectation _expectation = Expectation.Ignore;
}
