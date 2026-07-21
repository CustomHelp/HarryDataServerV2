using System.Globalization;
using System.IO;
using HarryDataServer.Communication;
using HarryDataServer.Configuration;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using HarryDataServer.Services;
using HarryShared.Data;

namespace HarryDataServer.Tests;

/// <summary>
/// Regression tests for the "Results" telegram measurement extraction
/// (<see cref="TelegramParser.ExtractMeasurements"/> + <see cref="MeasurementRowBuilder.Build"/>).
///
/// Root cause of the msa_measurements / production "value = 0/1, result_status NULL" defect
/// (found 2026-07-21): the live M2X/M5X JSON templates were off by one — they numbered the first
/// R_/V_ pair at telegram_place 71, but token 71 is <c>Total_Result</c> and the first measurement
/// pair starts at token 72 (CLAUDE.md §4; confirmed by live captures). The extraction/pairing CODE
/// is correct; only the template data was stale. These tests pin the correct 72-based numbering and
/// prove the old 71-based numbering scrambles value/status, so the defect cannot silently return.
///
/// Dependency-free on purpose (see the .csproj): a plain console runner, exit code = failure count.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        Console.WriteLine("HarryDataServer telegram-extraction regression tests\n");

        Correct72BasedTemplate_PairsRealValueWithStatus_Msa3();
        OldOffByOneTemplate_ScramblesValueAndStatus();
        Normal_Production_Correct72Based_YieldsRealValues();

        Msa3_NoLimits_ReportsToleranceReason_NotSilentZero();
        Msa3_OverThreshold_ReportsPctReason();
        Msa3_WithinThreshold_Passes();
        Msa1_TooFewValues_ReportsReason();
        MsaCalculator_Msa3_ComputesPctTolerance();
        MsaChannelForModule_MapsBothWays();
        OverallVerdict_NoVacuousPass();
        LimitSampleOverall_PerPartVerdict();
        LimitSampleReference_RoundTrips();
        ReportRunRoot_Layout();

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("ALL TESTS PASSED");
            return 0;
        }

        Console.WriteLine($"{_failures} ASSERTION(S) FAILED");
        return _failures;
    }

    /// <summary>
    /// Real MSA3 telegram (M50_ST110_KF1), correct 72-based template. Known tokens in the captured
    /// line: 72/73 = (+1, +0.008), 88/89 = (+0, -0.620), 118/119 = (+2, +99.000). Each R_/V_ pair
    /// must store the float in measurement_value and the SINT in result_status.
    /// </summary>
    private static void Correct72BasedTemplate_PairsRealValueWithStatus_Msa3()
    {
        Console.WriteLine("[Case A] MSA3 M50, correct 72-based template");
        var telegram = Parse("M50_ST110_KF1_MSA3_line1.txt");
        AssertEqual("mode", CameraOperatingMode.Msa3, telegram.Mode);
        AssertEqual("Total_Result (token 71, display only)", 0, telegram.OverallResult);

        var template = TemplateWith(
            (72, "R_FeatA", "Result"), (73, "V_FeatA", "Value"),
            (88, "R_FeatB", "Result"), (89, "V_FeatB", "Value"),
            (118, "R_FeatC", "Result"), (119, "V_FeatC", "Value"));

        var rows = ExtractRows(telegram, template, runType: 2 /* MSA3 */);

        AssertRow(rows, "FeatA", expectValue: 0.008, expectStatus: 1);
        AssertRow(rows, "FeatB", expectValue: -0.620, expectStatus: 0);
        AssertRow(rows, "FeatC", expectValue: 99.000, expectStatus: 2);
    }

    /// <summary>
    /// Same real telegram, but the OLD off-by-one template (first R_ at 71). This must reproduce the
    /// defect: R_FeatA reads Total_Result (token 71 = +0) → status 0, V_FeatA reads token 72 = +1 →
    /// value 1.0 (a status, not the real 0.008). Pins the bug so a regression is caught.
    /// </summary>
    private static void OldOffByOneTemplate_ScramblesValueAndStatus()
    {
        Console.WriteLine("[Case B] MSA3 M50, OLD 71-based template (must be scrambled)");
        var telegram = Parse("M50_ST110_KF1_MSA3_line1.txt");

        var template = TemplateWith((71, "R_FeatA", "Result"), (72, "V_FeatA", "Value"));
        var rows = ExtractRows(telegram, template, runType: 2);

        // The defining symptom: the status (1) lands in measurement_value, the real float is lost.
        AssertRow(rows, "FeatA", expectValue: 1.0, expectStatus: 0);
        var row = rows.Find(r => MeasurementRowBuilder.StripTypePrefix(r.VariableName) == "FeatA");
        AssertTrue("off-by-one loses the real value 0.008",
            row is not null && row.Value is not null && Math.Abs(row.Value.Value - 0.008) > 1e-9);
    }

    /// <summary>
    /// Real Normal-mode production telegram (M11_ST030_KF1) with the correct 72-based template. Proves
    /// the shared extraction path yields real values + statuses for production too (token 72/73 =
    /// +1, +0.043). Same code path as MSA — there is no separate production code to protect.
    /// </summary>
    private static void Normal_Production_Correct72Based_YieldsRealValues()
    {
        Console.WriteLine("[Case C] Normal M11 production, correct 72-based template");
        var telegram = Parse("M11_ST030_KF1_Normal_line1.txt");
        AssertEqual("mode", CameraOperatingMode.Normal, telegram.Mode);

        var template = TemplateWith((72, "R_GlueDot_1_Volume", "Result"), (73, "V_GlueDot_1_Volume", "Value"));
        var rows = ExtractRows(telegram, template, runType: 0 /* Normal */);

        AssertRow(rows, "GlueDot_1_Volume", expectValue: 0.043, expectStatus: 1);
    }

    // ---- MSA evaluation / reason tests (task B: never a silent 0/FAIL) ----

    private static void Msa3_NoLimits_ReportsToleranceReason_NotSilentZero()
    {
        Console.WriteLine("[Case D] MSA3 with no limits (tolerance=0) → FAIL with a tolerance reason");
        // This is the live root cause: the settings table is empty, so tolerance = 0.
        var (passed, reason) = MsaEvaluationText.Msa3Verdict(parts: 4, degreesOfFreedom: 8, tolerance: 0, pctTolerance: 0);
        AssertTrue("fails", !passed);
        AssertTrue("reason mentions limits/tolerance, not blank",
            reason.Contains("tolerance", StringComparison.OrdinalIgnoreCase) && reason.Length > 0);
        Console.WriteLine($"       reason = \"{reason}\"");
    }

    private static void Msa3_OverThreshold_ReportsPctReason()
    {
        Console.WriteLine("[Case E] MSA3 %P/T over 20% → FAIL with an explicit %P/T reason");
        var (passed, reason) = MsaEvaluationText.Msa3Verdict(parts: 4, degreesOfFreedom: 8, tolerance: 0.5, pctTolerance: 34.2);
        AssertTrue("fails", !passed);
        AssertTrue("reason shows the value and the limit", reason.Contains("34.2") && reason.Contains("20"));
        Console.WriteLine($"       reason = \"{reason}\"");
    }

    private static void Msa3_WithinThreshold_Passes()
    {
        Console.WriteLine("[Case F] MSA3 %P/T within 20% → pass, no reason");
        var (passed, reason) = MsaEvaluationText.Msa3Verdict(parts: 4, degreesOfFreedom: 8, tolerance: 0.5, pctTolerance: 12.0);
        AssertTrue("passes", passed);
        AssertTrue("no reason on clean pass", reason.Length == 0);
    }

    private static void Msa1_TooFewValues_ReportsReason()
    {
        Console.WriteLine("[Case G] MSA1 with n<2 → FAIL with an n reason");
        var (passed, reason) = MsaEvaluationText.Msa1Verdict(n: 1, sigma: 0, tolerance: 0.5, cg: 0, cgk: 0, hasReference: true);
        AssertTrue("fails", !passed);
        AssertTrue("reason mentions n", reason.Contains("n="));
        Console.WriteLine($"       reason = \"{reason}\"");
    }

    private static void MsaCalculator_Msa3_ComputesPctTolerance()
    {
        Console.WriteLine("[Case H] MsaCalculator.Msa3 sanity");
        // tolerance 0 → degenerate 0/false (the guard that produced the live all-zero FAIL).
        var zero = MsaCalculator.Msa3(new IReadOnlyList<double>[] { new double[] { 1, 2, 3 } }, tolerance: 0);
        AssertTrue("tolerance 0 → pct 0 & fail", zero.PctTolerance == 0 && !zero.Passed);
        // With variation and a real tolerance, %P/T is positive and finite.
        var r = MsaCalculator.Msa3(new IReadOnlyList<double>[]
        {
            new double[] { 10.0, 10.1, 9.9 },
            new double[] { 20.0, 20.2, 19.8 },
        }, tolerance: 5.0);
        AssertTrue("pct > 0 with variation", r.PctTolerance > 0);
    }

    private static void MsaChannelForModule_MapsBothWays()
    {
        Console.WriteLine("[Case I] MSA channel <-> module mapping (push target lookup)");
        AssertEqual("M50 -> MsaM50", SpsChannel.MsaM50, SpsChannelExtensions.MsaChannelForModule("M50"));
        AssertEqual("m20 (case-insensitive) -> MsaM20", SpsChannel.MsaM20, SpsChannelExtensions.MsaChannelForModule("m20"));
        AssertEqual("round-trips via ModuleKey", SpsChannel.MsaM11,
            SpsChannelExtensions.MsaChannelForModule(SpsChannel.MsaM11.ModuleKey()));
        AssertTrue("unknown module -> null", SpsChannelExtensions.MsaChannelForModule("XX") is null);
    }

    private static void OverallVerdict_NoVacuousPass()
    {
        Console.WriteLine("[Case J] Overall verdict — never a vacuous PASS (task 2)");
        // LimitSample: nothing evaluated → INVALID (was a false PASS before).
        AssertEqual("all not-evaluated → INVALID", MsaVerdict.Invalid,
            MsaEvaluationText.OverallVerdict(MsaType.LimitSample, true,
                new[] { Res(false, true), Res(false, true) }).Verdict);
        // LimitSample: evaluated but no prepared error to verify → INVALID.
        AssertEqual("no expected reject → INVALID", MsaVerdict.Invalid,
            MsaEvaluationText.OverallVerdict(MsaType.LimitSample, true,
                new[] { Res(true, true, expectedReject: false) }).Verdict);
        // LimitSample: a prepared error correctly rejected → PASS.
        AssertEqual("expected reject detected → PASS", MsaVerdict.Pass,
            MsaEvaluationText.OverallVerdict(MsaType.LimitSample, true,
                new[] { Res(true, true, expectedReject: true) }).Verdict);
        // LimitSample: a prepared error NOT rejected → FAIL.
        AssertEqual("expected reject missed → FAIL", MsaVerdict.Fail,
            MsaEvaluationText.OverallVerdict(MsaType.LimitSample, true,
                new[] { Res(true, false, expectedReject: true) }).Verdict);
        // MSA3: nothing evaluable (e.g. tolerance 0) → INVALID (not a vacuous pass).
        AssertEqual("MSA3 nothing evaluated → INVALID", MsaVerdict.Invalid,
            MsaEvaluationText.OverallVerdict(MsaType.Msa3, true, new[] { Res(false, false) }).Verdict);
    }

    private static MsaMeasurementResult Res(bool evaluated, bool passed, bool expectedReject = false) =>
        new() { DisplayName = "x", Controller = "c", Evaluated = evaluated, Passed = passed, ExpectedReject = expectedReject };

    private static void LimitSampleOverall_PerPartVerdict()
    {
        Console.WriteLine("[Case K] LimitSample per-part overall verdict (task A/2)");
        AssertEqual("no references → INVALID", MsaVerdict.Invalid,
            MsaEvaluationText.LimitSampleOverall(false, false, Array.Empty<MsaMeasurementResult>(), "dir").Verdict);
        AssertEqual("part without reference → INVALID", MsaVerdict.Invalid,
            MsaEvaluationText.LimitSampleOverall(true, true, new[] { Res(true, true, true) }, "dir").Verdict);
        AssertEqual("expected error detected → PASS", MsaVerdict.Pass,
            MsaEvaluationText.LimitSampleOverall(true, false, new[] { Res(true, true, expectedReject: true) }, "dir").Verdict);
        AssertEqual("expected error NOT detected → FAIL", MsaVerdict.Fail,
            MsaEvaluationText.LimitSampleOverall(true, false, new[] { Res(true, false, expectedReject: true) }, "dir").Verdict);
        AssertEqual("no expected error to verify → INVALID", MsaVerdict.Invalid,
            MsaEvaluationText.LimitSampleOverall(true, false, new[] { Res(true, true, expectedReject: false) }, "dir").Verdict);
    }

    private static void LimitSampleReference_RoundTrips()
    {
        Console.WriteLine("[Case L] Per-part reference file save/load/delete + DMC sanitize");
        var tmp = Path.Combine(Path.GetTempPath(), "hds_ls_" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = new LimitSampleReference { Dmc = "AB/CD:1", Module = "M50", TaughtAt = DateTime.UnixEpoch };
            r.Expected["F1"] = LimitSampleReference.ShouldFail;
            r.Expected["F2"] = LimitSampleReference.ShouldPass;
            var path = r.Save(tmp);

            AssertTrue("file created", File.Exists(path));
            AssertTrue("under <Module>\\LimitSamples", path.Replace('\\', '/').Contains("/M50/LimitSamples/"));
            var fileName = Path.GetFileName(path);
            AssertTrue("DMC sanitized in file name", !fileName.Contains('/') && !fileName.Contains(':'));

            var all = LimitSampleReference.LoadAll(tmp, "M50");
            AssertEqual("one taught part", 1, all.Count);
            AssertEqual("original DMC preserved", "AB/CD:1", all[0].Dmc);
            AssertEqual("one prepared error", 1, all[0].ExpectedRejectCount);

            AssertTrue("delete removes it", LimitSampleReference.Delete(tmp, "M50", "AB/CD:1"));
            AssertEqual("empty LimitSamples folder → INVALID feed", 0, LimitSampleReference.LoadAll(tmp, "M50").Count);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void ReportRunRoot_Layout()
    {
        Console.WriteLine("[Case M] Report run-root layout <root>\\<date>\\<module>\\<baseid>");
        var root = MsaResultLayout.ReportRunRoot(@"X:\R", "M50", "50260721170000", new DateTime(2026, 7, 21, 17, 0, 0));
        var norm = root.Replace('\\', '/');
        AssertTrue("layout ends with /2026-07-21/M50/50260721170000", norm.EndsWith("/2026-07-21/M50/50260721170000"));
    }

    // ---- helpers -----------------------------------------------------------

    private static ParsedTelegram Parse(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        var raw = File.ReadAllText(path).Trim('\r', '\n');
        var parser = new TelegramParser(new NullLog());
        var telegram = parser.ParseLine(raw);
        if (telegram is null)
        {
            Fail($"{fileName}: ParseLine returned null");
            throw new InvalidOperationException("unparseable telegram in test data");
        }
        return telegram;
    }

    private static List<PendingMeasurement> ExtractRows(ParsedTelegram telegram, ResultTemplateFile template, byte runType)
    {
        var parser = new TelegramParser(new NullLog());
        var samples = parser.ExtractMeasurements(telegram, template);
        return MeasurementRowBuilder.Build(
            cameraName: telegram.ControllerName,
            serial: telegram.Serial1,
            isTrimmer: false,
            runType: runType,
            measuredAt: DateTime.UnixEpoch,
            samples: samples);
    }

    private static ResultTemplateFile TemplateWith(params (int place, string name, string type)[] entries)
    {
        var t = new ResultTemplateFile { Camera = "TEST", SignalWord = "Results" };
        foreach (var (place, name, type) in entries)
        {
            t.Measurements.Add(new MeasurementTemplateEntry
            {
                TelegramPlace = place,
                VariableName = name,
                DisplayName = name,
                Type = type,
                Format = type == "Result" ? "SINT" : "Float",
                ParameterSet = 1,
            });
        }
        return t;
    }

    private static void AssertRow(List<PendingMeasurement> rows, string baseName, double expectValue, int expectStatus)
    {
        var row = rows.Find(r => MeasurementRowBuilder.StripTypePrefix(r.VariableName) == baseName);
        if (row is null)
        {
            Fail($"  {baseName}: no row produced");
            return;
        }

        var valueOk = row.Value is not null && Math.Abs(row.Value.Value - expectValue) < 1e-9;
        var statusOk = row.ResultStatus == expectStatus;
        var got = $"value={Fmt(row.Value)}, result_status={(row.ResultStatus?.ToString() ?? "NULL")}";
        if (valueOk && statusOk)
            Console.WriteLine($"  PASS {baseName}: {got}");
        else
            Fail($"  {baseName}: expected value={expectValue.ToString(CultureInfo.InvariantCulture)}, " +
                 $"result_status={expectStatus} but got {got}");
    }

    private static void AssertEqual<T>(string what, T expected, T actual)
    {
        if (Equals(expected, actual))
            Console.WriteLine($"  PASS {what} = {actual}");
        else
            Fail($"  {what}: expected {expected} but got {actual}");
    }

    private static void AssertTrue(string what, bool condition)
    {
        if (condition)
            Console.WriteLine($"  PASS {what}");
        else
            Fail($"  {what}: expected true");
    }

    private static string Fmt(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "NULL";

    private static void Fail(string message)
    {
        _failures++;
        Console.WriteLine("  FAIL " + message);
    }

    /// <summary>No-op <see cref="ILogService"/> so the parser can run without Serilog.</summary>
    private sealed class NullLog : ILogService
    {
        public void Debug(string message, params object?[] propertyValues) { }
        public void Information(string message, params object?[] propertyValues) { }
        public void Warning(string message, params object?[] propertyValues) { }
        public void Error(string message, params object?[] propertyValues) { }
        public void Error(Exception exception, string message, params object?[] propertyValues) { }
        public void Shutdown() { }
    }
}
