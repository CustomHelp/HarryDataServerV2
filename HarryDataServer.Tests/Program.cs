using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HarryDataServer.Communication;
using HarryDataServer.Controls;
using HarryDataServer.ViewModels;
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

    // STA is required for the WPF view-mechanics case (TailScrollView is laid out headlessly).
    [STAThread]
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
        Msa1Matcher_BestMatch();
        Msa1Reference_TemplatesAndCandidates();
        PerPartPdfName_ContainsBaseIdAndDmc();
        PartAggregation_WorstOfParts();
        MirrorModules_ShareReferences();
        LimitSample_BothDirections();
        LimitSample_Criterion_MentionsBothDirections();
        LimitSample_PartWithoutReference_IsInvalid();
        LimitSample_PartialRun_IsInvalidNotPass();
        LimitSample_GoodReferenceAllowed();

        TrimmerSerial_NormalizesTo13_FramesStayAt19();
        PartExit_TrimmerNormalisedTo13();
        PartExit_DeParsesAsDeleted();
        DeImageDelete_MatchesFrameAndTrimmer_NotAdjacent();
        ImageSearchKeys_AreUnderscoreFree_AndMatchRealFilenames();
        ImageName_ParsesAllSixFields_AllLiveForms();
        ImageMatching_IsFieldAccurate_NotSubstring();
        MsaImages_AreProtectedFromProductionFlows();
        M2xAndLegacy_ImagesAreFoundByTheirOwnSerial();
        PartExit_ImageActionsFollowTheTargetConcept();
        PartExit_OkFollowsCollageGenerateOnly();
        PartExit_UnknownResult_NoCsvButDbImagesAndWarning();
        PartExit_EmptySzid_WritesNoDmcserialRow();
        DeprecatedIniKeys_AreReportedNotSilentlyHonoured();
        PartExitAck_StaysByteIdentical_DespiteTheMsDisplay();
        MsaRunImages_AreMovedNotCopied();
        TailScroll_FollowsPausesCountsAndResumes();
        DePartExit_NoDbNoCsv_DeletesImages_Logs();
        BackupRoot_NonInputPathIsItsOwnRoot();
        Retention_NewSectionWins_And_ZeroMeansNever();
        Retention_LegacyKeysFallBack_WithDeprecation();

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

    private static void Msa1Matcher_BestMatch()
    {
        Console.WriteLine("[Case N] MSA1 best-match (unique / ambiguous / no-match)");
        var tol = new Dictionary<string, double> { ["A"] = 1.0, ["B"] = 1.0 };
        var means = new Dictionary<string, double> { ["A"] = 10.0, ["B"] = 20.0 };
        var good = new Msa1Matcher.Candidate("Ref A", "a.json", new Dictionary<string, double> { ["A"] = 10.02, ["B"] = 20.03 });
        var far = new Msa1Matcher.Candidate("Ref far", "f.json", new Dictionary<string, double> { ["A"] = 13.0, ["B"] = 25.0 });
        var good2 = new Msa1Matcher.Candidate("Ref A2", "a2.json", new Dictionary<string, double> { ["A"] = 10.02, ["B"] = 20.03 });

        var unique = Msa1Matcher.Match(means, tol, new[] { good, far });
        AssertTrue("unique best is Ref A", unique.Best?.File == "a.json" && unique.Plausible && !unique.Ambiguous);

        var ambiguous = Msa1Matcher.Match(means, tol, new[] { good, good2 });
        AssertTrue("two equal candidates → ambiguous", ambiguous.Ambiguous);

        var none = Msa1Matcher.Match(means, tol, new[] { far });
        AssertTrue("far-off candidate → not plausible", !none.Plausible);
    }

    private static void Msa1Reference_TemplatesAndCandidates()
    {
        Console.WriteLine("[Case O] MSA1 reference: DEMO templates ignored, real ones are candidates");
        var tmp = Path.Combine(Path.GetTempPath(), "hds_msa1_" + Guid.NewGuid().ToString("N"));
        try
        {
            var real = new Msa1Reference { Module = "M50", Label = "Ref A", CreatedAt = DateTime.UnixEpoch };
            real.Values["A"] = 1.0;
            real.Save(tmp, "RefA");

            var demoByFlag = new Msa1Reference { Module = "M50", Template = true };
            demoByFlag.Save(tmp, "DEMO_M50");

            var demoByName = new Msa1Reference { Module = "M50", Template = false }; // template only by DEMO_ file name
            demoByName.Save(tmp, "DEMO_extra");

            AssertEqual("loads all 3 files", 3, Msa1Reference.LoadAll(tmp, "M50").Count);
            var candidates = Msa1Reference.LoadCandidates(tmp, "M50");
            AssertEqual("only the real one is a candidate", 1, candidates.Count);
            AssertEqual("candidate label", "Ref A", candidates[0].Label);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void PerPartPdfName_ContainsBaseIdAndDmc()
    {
        Console.WriteLine("[Case P] Per-part PDF file name contains BaseID + DMC (task B4)");
        var tmp = Path.Combine(Path.GetTempPath(), "hds_pdf_" + Guid.NewGuid().ToString("N"));
        var pdf = new PdfReportService(new StubConfig(), new NullLog());
        var report = new MsaReportData
        {
            Module = "M50", TestType = "LimitSample", BaseId = "50260721170000", Dmc = "AB/CD",
            RunAt = new DateTime(2026, 7, 21, 17, 0, 0), OutputDirectory = tmp,
        };
        var paths = pdf.ResolvePaths(report);
        var name = Path.GetFileName(paths.AllResults);
        AssertTrue("name has BaseID", name.Contains("50260721170000"));
        AssertTrue("name has sanitized DMC", name.Contains("AB_CD"));
        AssertTrue("name ends _AllResults.pdf", name.EndsWith("_AllResults.pdf"));
    }

    private static void PartAggregation_WorstOfParts()
    {
        Console.WriteLine("[Case Q] Overall = worst of per-part verdicts (task A)");
        AssertEqual("any INVALID → INVALID", MsaVerdict.Invalid,
            MsaEvaluationText.OverallFromParts(new[] { ("p1", MsaVerdict.Pass), ("p2", MsaVerdict.Invalid), ("p3", MsaVerdict.Fail) }).Verdict);
        AssertEqual("any FAIL (no invalid) → FAIL", MsaVerdict.Fail,
            MsaEvaluationText.OverallFromParts(new[] { ("p1", MsaVerdict.Pass), ("p2", MsaVerdict.Fail) }).Verdict);
        AssertEqual("all PASS → PASS", MsaVerdict.Pass,
            MsaEvaluationText.OverallFromParts(new[] { ("p1", MsaVerdict.Pass), ("p2", MsaVerdict.Pass) }).Verdict);
    }

    private static void MirrorModules_ShareReferences()
    {
        Console.WriteLine("[Case R] Baugleich mirror shares LimitSample + MSA1 references (M10<->M11)");
        AssertEqual("MirrorOf M10 = M11", "M11", ModuleMirror.MirrorOf("M10"));
        AssertEqual("MirrorOf M11 = M10", "M10", ModuleMirror.MirrorOf("M11"));
        AssertTrue("MirrorOf M50 = null", ModuleMirror.MirrorOf("M50") is null);

        var tmp = Path.Combine(Path.GetTempPath(), "hds_mirror_" + Guid.NewGuid().ToString("N"));
        try
        {
            // A LimitSample part taught on M11 must be visible when loading references for M10.
            var ls = new LimitSampleReference { Dmc = "MIRROR-DMC-1", Module = "M11", TaughtAt = DateTime.UnixEpoch };
            ls.Expected["A"] = LimitSampleReference.ShouldFail;
            ls.Save(tmp);
            var seenFromM10 = LimitSampleReference.LoadAllWithMirror(tmp, "M10");
            AssertTrue("M10 sees the M11 LimitSample part", seenFromM10.Any(r => r.Dmc == "MIRROR-DMC-1"));

            // An MSA1 reference taught on M11 must be a best-match candidate for M10.
            var m1 = new Msa1Reference { Module = "M11", Label = "Mirror Ref" };
            m1.Values["A"] = 1.0;
            m1.Save(tmp, "MirrorRef");
            var candFromM10 = Msa1Reference.LoadCandidatesWithMirror(tmp, "M10");
            AssertTrue("M10 gets the M11 MSA1 candidate", candFromM10.Any(c => c.Label == "Mirror Ref"));

            // A module without a mirror is unaffected (no partner folder pulled in).
            AssertEqual("M50 has no mirror parts", 0, LimitSampleReference.LoadAllWithMirror(tmp, "M50").Count);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void LimitSample_BothDirections()
    {
        Console.WriteLine("[Case S] LimitSample evaluated in BOTH directions (task A4)");
        // Prepared error (ShouldFail) MUST be rejected.
        AssertTrue("ShouldFail + rejected → pass", MsaEvaluationText.LimitSampleFeature(shouldFail: true, wasRejected: true).Passed);
        AssertTrue("ShouldFail + accepted → FAIL", !MsaEvaluationText.LimitSampleFeature(shouldFail: true, wasRejected: false).Passed);
        // Good feature (ShouldPass) MUST be accepted.
        AssertTrue("ShouldPass + accepted → pass", MsaEvaluationText.LimitSampleFeature(shouldFail: false, wasRejected: false).Passed);
        AssertTrue("ShouldPass + rejected → FAIL", !MsaEvaluationText.LimitSampleFeature(shouldFail: false, wasRejected: true).Passed);
        var reason = MsaEvaluationText.LimitSampleFeature(shouldFail: false, wasRejected: true).Reason;
        AssertTrue("good-feature rejection has a reason", reason.Contains("good feature", StringComparison.OrdinalIgnoreCase));
    }

    private static void LimitSample_Criterion_MentionsBothDirections()
    {
        Console.WriteLine("[Case T] LimitSample criterion text names both directions (task A4)");
        var c = MsaEvaluationText.Criterion(MsaType.LimitSample);
        AssertTrue("mentions rejected", c.Contains("rejected", StringComparison.OrdinalIgnoreCase));
        AssertTrue("mentions accepted", c.Contains("accepted", StringComparison.OrdinalIgnoreCase));
    }

    private static void LimitSample_PartWithoutReference_IsInvalid()
    {
        Console.WriteLine("[Case U] LimitSample part with no reference file → INVALID (task A3)");
        // A part that produced no reference gets a single synthetic InvalidatesPart row.
        var noRef = new[] { new MsaMeasurementResult { DisplayName = "(part not referenced)", Dmc = "D1",
            Evaluated = false, Passed = false, InvalidatesPart = true } };
        AssertEqual("no-reference part → INVALID", MsaVerdict.Invalid,
            MsaEvaluationText.PartVerdict(MsaType.LimitSample, noRef));
    }

    private static void LimitSample_PartialRun_IsInvalidNotPass()
    {
        Console.WriteLine("[Case V] LimitSample full run with unreferenced parts → INVALID, never a partial PASS (task A1)");
        // Reproduces the reported bug at the verdict level: one part has a reference and its prepared
        // error was rejected (would be PASS alone — the premature 1-part evaluation), the other three
        // parts have no reference. The COMPLETE run must be INVALID (worst-of-parts), not OK.
        var good = MsaEvaluationText.PartVerdict(MsaType.LimitSample,
            new[] { Res(true, true, expectedReject: true) });
        AssertEqual("the single good part alone would be PASS", MsaVerdict.Pass, good);

        var parts = new[]
        {
            ("good", good),
            ("noref1", MsaVerdict.Invalid),
            ("noref2", MsaVerdict.Invalid),
            ("noref3", MsaVerdict.Invalid),
        };
        AssertEqual("complete run with unreferenced parts → INVALID", MsaVerdict.Invalid,
            MsaEvaluationText.OverallFromParts(parts).Verdict);
    }

    private static MsaResultRow LsRow(string dmc, bool passed, bool expectedReject, bool evaluated = true) =>
        new() { DisplayName = "x", Controller = "c", Dmc = dmc, Evaluated = evaluated, Passed = passed,
                Expected = expectedReject ? "reject" : "accept" };

    private static void LimitSample_GoodReferenceAllowed()
    {
        Console.WriteLine("[Case W] LimitSample allows GOOD reference parts (task A)");

        // Gut-Teil, alles ok → PASS, labelled "Gut-Referenz".
        var good = MsaEvaluationText.PartVerdictDetailed(MsaType.LimitSample, new[] { Res(true, true, expectedReject: false) });
        AssertEqual("good part → PASS", MsaVerdict.Pass, good.Verdict);
        AssertTrue("good part labelled good reference", good.Reason.Contains("good reference"));

        // Gut-Teil mit einem NOK (Falsch-Ausschuss) → FAIL, Merkmal im Grund.
        var falseReject = MsaEvaluationText.PartVerdictDetailed(MsaType.LimitSample,
            new[] { Res(true, true, expectedReject: false), Res(true, false, expectedReject: false) });
        AssertEqual("good part with a NOK → FAIL", MsaVerdict.Fail, falseReject.Verdict);
        AssertTrue("FAIL reason names the feature", falseReject.Reason.Contains("not as expected"));

        // Run of only good samples (no expected error checked) → INVALID with a reason (task A2).
        var allGood = MsaReportData.ComputeVerdict(MsaType.LimitSample,
            new[] { LsRow("D1", true, expectedReject: false), LsRow("D2", true, expectedReject: false) }, wholeRun: true);
        AssertEqual("run of only good samples → INVALID", MsaVerdict.Invalid, allGood.Verdict);
        AssertTrue("INVALID reason = only good samples", allGood.Reason.Contains("only good samples"));

        // Mixed run (good part + checked error detected) → PASS.
        var mixed = MsaReportData.ComputeVerdict(MsaType.LimitSample,
            new[] { LsRow("D1", true, expectedReject: true), LsRow("D2", true, expectedReject: false) }, wholeRun: true);
        AssertEqual("mixed run (good + detected reject) → PASS", MsaVerdict.Pass, mixed.Verdict);

        // Ein Falsch-Ausschuss in einem reinen Gut-Lauf → FAIL, nicht INVALID (die Abweichung ist real).
        var goodRunWithNok = MsaReportData.ComputeVerdict(MsaType.LimitSample,
            new[] { LsRow("D1", false, expectedReject: false) }, wholeRun: true);
        AssertEqual("all-good run with a false reject → FAIL (not INVALID)", MsaVerdict.Fail, goodRunWithNok.Verdict);
    }

    // ---- Trimmer serial length + DE image delete + backup retention (2026-07-27) ----

    private static void TrimmerSerial_NormalizesTo13_FramesStayAt19()
    {
        Console.WriteLine("[Case X] Trimmer serial normalises to 13, frame stays 19");
        SerialNumberHelper.Configure(19);
        SerialNumberHelper.ConfigureTrimmer(13);

        // The camera pads the 13-char trimmer serial with trailing '0' to the field width.
        AssertEqual("padded trimmer → 13", "2607230000810",
            SerialNumberHelper.NormalizeTrimmer("2607230000810000000"));
        // Already 13 (as the SPS delivers it) → unchanged (idempotent).
        AssertEqual("bare 13 trimmer unchanged", "2607230000810",
            SerialNumberHelper.NormalizeTrimmer("2607230000810"));
        // A non-zero tail past 13 is a genuinely longer serial → never blindly trimmed.
        AssertEqual("non-zero tail preserved", "2607230000810500000",
            SerialNumberHelper.NormalizeTrimmer("2607230000810500000"));
        // The frame normaliser still keeps 19 for a padded frame serial.
        AssertEqual("frame stays 19", "2707261005160030078",
            SerialNumberHelper.Normalize("2707261005160030078" + "000"));
        // Two adjacent trimmer serials (differ only in the 13th char) stay distinct after normalising —
        // this is why DE image matching must use the full 13, not a 12-char key.
        AssertTrue("adjacent trimmers stay distinct",
            SerialNumberHelper.NormalizeTrimmer("2607230000810000") != SerialNumberHelper.NormalizeTrimmer("2607230000811000"));
    }

    private static void PartExit_TrimmerNormalisedTo13()
    {
        Console.WriteLine("[Case Y] Part-exit VirtualSerial normalised to 13, SZID to 19");
        SerialNumberHelper.Configure(19);
        SerialNumberHelper.ConfigureTrimmer(13);
        var szid = "2707261005160030078";
        var trimmerPadded = "2607230000810" + "000000000"; // camera-style padding
        var telegram = string.Join(";",
            "", szid, trimmerPadded, "1117", "Normal", "50", "1", "20", "2", "30", "3", "4", "1.0", "2.0", "OK");
        var data = SpsPartExitData.TryParse(telegram);
        AssertTrue("parses", data is not null);
        AssertEqual("SZID length 19", szid, data!.Szid);
        AssertEqual("trimmer normalised to 13", "2607230000810", data.VirtualSerial);
    }

    private static void PartExit_DeParsesAsDeleted()
    {
        Console.WriteLine("[Case Z] Part-exit DE (SZID empty, trimmer only) → Deleted, result_status -1");
        var trimmer = "2607230000810";
        var telegram = string.Join(";",
            "", "", trimmer, "1117", "Normal", "20", "2", "20", "2", "30", "3", "4", "1.0", "2.0", "DE");
        var data = SpsPartExitData.TryParse(telegram);
        AssertTrue("parses", data is not null);
        AssertEqual("result = Deleted", PartResult.Deleted, data!.Result);
        AssertEqual("result_status code = -1", -1, data.ResultStatusCode);
        AssertEqual("trimmer carried", trimmer, data.VirtualSerial);
    }

    private static void DeImageDelete_MatchesFrameAndTrimmer_NotAdjacent()
    {
        Console.WriteLine("[Case AA] DE image delete: frame SZID + trimmer, padded Field1, sorted day-folders, no adjacent spill");
        var root = Path.Combine(Path.GetTempPath(), "hds_de_" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "Input");
        var day = Path.Combine(root, "2026", "07", "23");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(day);
        try
        {
            const string szid = "2707261602460031771";  // 19-char frame serial
            const string trimmer = "2607230000810";       // 13-char trimmer serial
            const string adjacent = "2607230000811";       // differs only in the 13th char

            // Real Field 1 form on the line: the serial right-padded with '0' to the field width
            // (no separators), then the hyphen-delimited tail.
            string Name(string serial, string ctrl) =>
                $"{serial.PadRight(32, '0')}-{new string('0', 32)}-1-{ctrl}-1-&Cam1Img.bmp";

            var frameFile = Path.Combine(day, Name(szid, "M50_ST040_KF1"));    // frame image in a sorted day-folder
            var trimmerFile = Path.Combine(input, Name(trimmer, "M20_ST060_KF1"));
            var adjacentFile = Path.Combine(input, Name(adjacent, "M20_ST060_KF1"));
            var unrelated = Path.Combine(input, Name("2707269999990039999", "M50_ST040_KF1"));
            foreach (var f in new[] { frameFile, trimmerFile, adjacentFile, unrelated })
                File.WriteAllText(f, "x");
            File.WriteAllText(Path.Combine(input, "readme.txt"), "x"); // no Field1 -> ignored

            var handler = new ImageHandler(new NullLog());
            // Pass the ...\Input path (like the config): SortedRoot expands it so both \Input and the
            // legacy YYYY\MM\DD day-folder are searched. Delete by BOTH the frame and the trimmer serial.
            var result = handler
                .ApplyAsync("DE", new[] { szid, trimmer }, input, PartImageAction.Delete,
                    string.Empty, CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertEqual("deleted frame + trimmer image", 2, result.Handled);
            AssertTrue("inspected count reported for the diagnostic log", result.Inspected >= 4);
            AssertTrue("frame image (day-folder) gone", !File.Exists(frameFile));
            AssertTrue("trimmer image gone", !File.Exists(trimmerFile));
            AssertTrue("adjacent trimmer survived (no 13th-char spill)", File.Exists(adjacentFile));
            AssertTrue("unrelated part survived", File.Exists(unrelated));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void ImageSearchKeys_AreUnderscoreFree_AndMatchRealFilenames()
    {
        Console.WriteLine("[Case AF] Image search keys are underscore-free and match real Keyence filenames");
        SerialNumberHelper.Configure(19);
        SerialNumberHelper.ConfigureTrimmer(13);

        const string szid = "2707261603210031811";  // 19-char frame serial (live example)
        const string trimmer = "2607270001640";       // 13-char trimmer serial (live example)

        // 1) The key build (part exit → collage/image search) must never decorate the serial.
        var part = SpsPartExitData.TryParse(string.Join(";",
            "", szid, trimmer, "1117", "Normal", "50", "1", "20", "2", "30", "3", "4", "1.0", "2.0", "OK"))!;
        var keys = CollageService.SearchSerials(part);
        AssertEqual("frame + trimmer key", 2, keys.Count);
        AssertEqual("frame key is the bare 19-char serial", szid, keys[0]);
        AssertEqual("trimmer key is the full 13-char serial", trimmer, keys[1]);
        AssertTrue("no key contains an underscore", keys.All(k => !k.Contains('_')));

        // 2) Central sanitiser: a decorated value (historic "_ after char 12") is repaired, and the
        //    serial length is preserved — no blind TrimEnd('0'), so a 12-char prefix cannot spill.
        AssertEqual("underscore stripped (frame)", szid, SerialNumberHelper.ToImageSearchKey("270726160321_0031811"));
        AssertEqual("underscore stripped (trimmer)", trimmer, SerialNumberHelper.ToImageSearchKey("260727000164_0"));
        AssertEqual("trailing '0' of a real serial kept", trimmer, SerialNumberHelper.ToImageSearchKey(trimmer));
        AssertEqual("empty stays empty", string.Empty, SerialNumberHelper.ToImageSearchKey("  "));

        // 3) End to end against real filenames: even a decorated input must find + delete the images.
        var root = Path.Combine(Path.GetTempPath(), "hds_key_" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "Input");
        Directory.CreateDirectory(input);
        try
        {
            // Exactly as the controller writes them (Field 1 = serial right-padded with '0').
            string Name(string serial, string ctrl) =>
                $"{serial.PadRight(22, '0')}-{new string('0', 31)}-1-{ctrl}-1-&Cam1Img.bmp";

            var frameFile = Path.Combine(input, Name(szid, "M50_ST140_KF1"));
            var trimmerFile = Path.Combine(input, Name(trimmer, "M20_ST060_KF1"));
            var unrelated = Path.Combine(input, Name("2707269999990039999", "M50_ST040_KF1"));
            foreach (var f in new[] { frameFile, trimmerFile, unrelated })
                File.WriteAllText(f, "x");

            var handler = new ImageHandler(new NullLog());
            var ok = handler.ApplyAsync("OK",
                    new[] { "270726160321_0031811", "260727000164_0" }, // decorated on purpose
                    input, PartImageAction.Delete, string.Empty, CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertEqual("image handling reports no failure", 0, ok.Failed);
            AssertTrue("frame image found + deleted despite the '_' in the input", !File.Exists(frameFile));
            AssertTrue("trimmer image found + deleted despite the '_' in the input", !File.Exists(trimmerFile));
            AssertTrue("unrelated part survived", File.Exists(unrelated));

            // Same for the DE purge path (Serial1 prefix match).
            var deFile = Path.Combine(input, Name(szid, "M50_ST130_KF1"));
            File.WriteAllText(deFile, "x");
            var de = handler
                .ApplyAsync("DE", new[] { "270726160321_0031811" }, input, PartImageAction.Delete,
                    string.Empty, CancellationToken.None)
                .GetAwaiter().GetResult();
            AssertEqual("DE purge deletes despite the '_' in the input", 1, de.Handled);
            AssertEqual("DE purge reports the normalised key", szid, de.Keys[0]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- Image filename spec (Philipp 2026-07-28) + field-accurate matching -----------------------

    private static void ImageName_ParsesAllSixFields_AllLiveForms()
    {
        Console.WriteLine("[Case AG] Image filename parser: all 6 fields, all forms found on the live line");

        // 1) Spec form (M50, and M2X camera 2): Serial1 = 22, Serial2 = 32.
        var std = ImageFileName.TryParse("2707261538450031633000-00000000000000000000000000000000-1-M50_ST140_KF1-2-&Cam2Img.bmp");
        AssertTrue("standard parses", std is not null);
        AssertEqual("Serial1", "2707261538450031633000", std!.Serial1);
        AssertEqual("Serial1 width 22", 22, std.Serial1.Length);
        AssertEqual("Serial2 width 32", 32, std.Serial2.Length);
        AssertEqual("Overall", "1", std.Overall);
        AssertEqual("Controller (keeps its underscores)", "M50_ST140_KF1", std.Controller);
        AssertEqual("CameraNumber", "2", std.CameraNumber);
        AssertEqual("ImageVariable", "&Cam2Img.bmp", std.ImageName);
        AssertEqual("form", ImageNameForm.Standard, std.Form);
        AssertTrue("normal mode → not MSA", !std.IsMsa);

        // Image variable with an underscore must not be mistaken for a field separator.
        var dark = ImageFileName.TryParse("2707261603210031811000-00000000000000000000000000000000-1-M50_ST130_KF1-1-&Cam1Img_Dark.bmp");
        AssertEqual("&Cam1Img_Dark kept whole", "&Cam1Img_Dark.bmp", dark!.ImageName);
        AssertEqual("controller unaffected", "M50_ST130_KF1", dark.Controller);

        // 2) M20/M21 camera 1 writes the two widths SWAPPED (32/22) — live fact, ~2000 files/day.
        var swapped = ImageFileName.TryParse("26072700015510000000000000000000-0000000000000000000000-1-M20_ST060_KF3-1-&Cam1Img.bmp");
        AssertTrue("swapped-width name parses", swapped is not null);
        AssertEqual("swapped form detected", ImageNameForm.SwappedWidths, swapped!.Form);
        AssertEqual("Serial1 is the 32-char field", 32, swapped.Serial1.Length);
        AssertTrue("trimmer serial still the Serial1 prefix", swapped.MatchesSerial1Prefix("2607270001551"));
        AssertTrue("zeros in Serial2 → not MSA", !swapped.IsMsa);

        // 3) Legacy V1 form, still written by M50_ST040_KF1 for OCR images (~500/day into the NG folder).
        var legacy = ImageFileName.TryParse("270726161219_00320440000000000000_1_M50_ST040_KF1_2_OCR_&Cam2Img_Dark.png");
        AssertTrue("legacy underscore name parses", legacy is not null);
        AssertEqual("legacy form detected", ImageNameForm.LegacyUnderscore, legacy!.Form);
        AssertEqual("controller", "M50_ST040_KF1", legacy.Controller);
        AssertEqual("camera number", "2", legacy.CameraNumber);
        AssertEqual("overall", "1", legacy.Overall);
        AssertEqual("image variable", "&Cam2Img_Dark.png", legacy.ImageName);
        AssertTrue("serial reassembled without the '_' → same key as the modern form",
            legacy.MatchesSerial1Prefix("2707261612190032044"));
        AssertTrue("legacy has no second serial → not MSA", !legacy.IsMsa);

        // 4) MSA form: Serial2 is the real DMC — and may itself contain hyphens.
        var msa = ImageFileName.TryParse("50260721170000001000-0-26-0726-1225564444444400000012-1-M50_ST110_KF1-1-&Cam1Img.png");
        AssertTrue("MSA name with hyphens in the DMC parses", msa is not null);
        AssertEqual("DMC kept intact incl. hyphens", "26-0726-1225564444444400000012", msa!.Serial2);
        AssertEqual("controller after a hyphenated DMC", "M50_ST110_KF1", msa.Controller);
        AssertEqual("camera number after a hyphenated DMC", "1", msa.CameraNumber);
        AssertTrue("non-zero Serial2 → MSA", msa.IsMsa);

        // 5) Off-spec / unparsable names: defined fallback, never a silent match.
        AssertTrue("plain text file → null", ImageFileName.TryParse("readme.txt") is null);
        AssertTrue("no image-variable anchor → null", ImageFileName.TryParse("2707261538450031633000-0000-1-M50-1.bmp") is null);
        AssertTrue("empty → null", ImageFileName.TryParse(string.Empty) is null);
    }

    private static void ImageMatching_IsFieldAccurate_NotSubstring()
    {
        Console.WriteLine("[Case AH] Matching is field-accurate: a trimmer key inside the DMC must NOT hit");

        const string trimmer = "2607270001640";  // 13-char trimmer serial
        // An MSA image whose DMC (Serial2) happens to CONTAIN that trimmer serial as a substring.
        var dmc = ("999" + trimmer + "88888888888888").PadRight(32, '8');
        var msaName = $"{"50260728120000001".PadRight(22, '0')}-{dmc}-1-M20_ST060_KF1-1-&Cam1Img.bmp";
        var msa = ImageFileName.TryParse(msaName)!;

        AssertTrue("old V1 semantics WOULD have matched (substring of the whole name)",
            msaName.Contains(trimmer, StringComparison.Ordinal));
        AssertTrue("field-accurate: trimmer key does NOT match Serial1", !msa.MatchesSerial1Prefix(trimmer));
        AssertTrue("recognised as MSA", msa.IsMsa);

        // The production image of that same trimmer DOES match — by Serial1 prefix.
        var prod = ImageFileName.TryParse($"{trimmer.PadRight(22, '0')}-{new string('0', 32)}-1-M20_ST060_KF1-2-&Cam2Img.bmp")!;
        AssertTrue("production trimmer image matches by Serial1 prefix", prod.MatchesSerial1Prefix(trimmer));
        AssertTrue("adjacent trimmer does not match", !prod.MatchesSerial1Prefix("2607270001641"));

        // MSA searches: BaseID against Serial1, DMC against Serial2 — each in its own field.
        AssertTrue("MSA run image found by BaseID (Serial1)", msa.MatchesBaseIdField("50260728120000"));
        AssertTrue("MSA run image found by DMC (Serial2)", msa.MatchesDmcField(dmc));
        AssertTrue("BaseID is not searched in Serial2", !msa.MatchesDmcField("50260728120000"));
    }

    private static void MsaImages_AreProtectedFromProductionFlows()
    {
        Console.WriteLine("[Case AI] MSA images survive DE purge, OK backup/delete and the Input-leftover sweep");

        SerialNumberHelper.Configure(19);
        SerialNumberHelper.ConfigureTrimmer(13);
        const string szid = "2807260753160032218";

        var root = Path.Combine(Path.GetTempPath(), "hds_msa_" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "Input");
        Directory.CreateDirectory(input);
        try
        {
            // Worst case on purpose: the MSA image's Serial1 starts with the very serial being purged,
            // so ONLY the MSA marker (Serial2 = DMC) can save it.
            var msaImg = Path.Combine(input,
                $"{szid.PadRight(22, '0')}-{"2607261225564444444400000012XYZ".PadRight(32, '7')}-1-M50_ST110_KF1-1-&Cam1Img.bmp");
            var prodImg = Path.Combine(input,
                $"{szid.PadRight(22, '0')}-{new string('0', 32)}-1-M50_ST130_KF1-1-&Cam1Img.bmp");
            File.WriteAllText(msaImg, "x");
            File.WriteAllText(prodImg, "x");

            var log = new RecordingLog();
            var handler = new ImageHandler(log);

            // a) DE purge deletes the production image, keeps the MSA image.
            var de = handler.ApplyAsync("DE", new[] { szid }, input, PartImageAction.Delete,
                    string.Empty, CancellationToken.None)
                .GetAwaiter().GetResult();
            AssertEqual("DE deleted only the production image", 1, de.Handled);
            AssertEqual("MSA image counted as skipped", 1, de.MsaSkipped);
            AssertTrue("DE kept the MSA image", File.Exists(msaImg));
            AssertTrue("DE deleted the production image", !File.Exists(prodImg));

            // b) OK-part handling (both actions) must not touch the MSA image either.
            var backup = Path.Combine(root, "Backup");
            var ok = handler.ApplyAsync("OK", new[] { szid }, input, PartImageAction.MoveToBackup,
                    backup, CancellationToken.None)
                .GetAwaiter().GetResult();
            AssertEqual("OK image handling reports no failure", 0, ok.Failed);
            AssertTrue("OK handling kept the MSA image", File.Exists(msaImg));
            AssertTrue("MSA image was NOT copied into the backup tree",
                !Directory.Exists(backup) || !Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).Any());

            // c) The 3-day Input-leftover retention keeps MSA images in 01-04 (and reports them) …
            AssertEqual("leftover sweep keeps MSA", RetentionService.LeftoverAction.KeepMsa,
                RetentionService.ClassifyLeftover(Path.GetFileName(msaImg), skipNgFlagged: true));
            AssertEqual("leftover sweep deletes an OK production leftover", RetentionService.LeftoverAction.Delete,
                RetentionService.ClassifyLeftover(Path.GetFileName(prodImg), skipNgFlagged: true));
            AssertEqual("leftover sweep keeps an NG low-res image", RetentionService.LeftoverAction.KeepNg,
                RetentionService.ClassifyLeftover(
                    $"{szid.PadRight(22, '0')}-{new string('0', 32)}-0-M50_ST130_KF1-1-&Cam1Img.bmp", skipNgFlagged: true));
            AssertEqual("leftover sweep keeps an unknown name", RetentionService.LeftoverAction.KeepUnknown,
                RetentionService.ClassifyLeftover("something_else.txt", skipNgFlagged: false));

            // … but 05_GoldenSample is a TRANSIT folder: there both MSA and production leftovers go.
            AssertEqual("05 transit: MSA leftover IS deleted", RetentionService.LeftoverAction.Delete,
                RetentionService.ClassifyLeftover(Path.GetFileName(msaImg), skipNgFlagged: false, isMsaTransitFolder: true));
            AssertEqual("05 transit: M1X production leftover IS deleted", RetentionService.LeftoverAction.Delete,
                RetentionService.ClassifyLeftover(Path.GetFileName(prodImg), skipNgFlagged: false, isMsaTransitFolder: true));
            AssertEqual("05 transit: an unparsable name is STILL kept", RetentionService.LeftoverAction.KeepUnknown,
                RetentionService.ClassifyLeftover("something_else.txt", skipNgFlagged: false, isMsaTransitFolder: true));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void M2xAndLegacy_ImagesAreFoundByTheirOwnSerial()
    {
        Console.WriteLine("[Case AJ] M2X (virtual serial) and the legacy OCR form are found by the DE purge");

        SerialNumberHelper.Configure(19);
        SerialNumberHelper.ConfigureTrimmer(13);
        const string szid = "2707261612190032044";   // frame serial of the part
        const string trimmer = "2607270001551";        // its trimmer serial

        var root = Path.Combine(Path.GetTempPath(), "hds_m2x_" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "Input");
        Directory.CreateDirectory(input);
        try
        {
            // M2X camera 1 (swapped widths) and camera 2 (spec widths) — both carry the TRIMMER serial.
            var m2xCam1 = Path.Combine(input, $"{trimmer.PadRight(32, '0')}-{new string('0', 22)}-1-M20_ST060_KF3-1-&Cam1Img.bmp");
            var m2xCam2 = Path.Combine(input, $"{trimmer.PadRight(22, '0')}-{new string('0', 32)}-1-M20_ST060_KF3-2-&Cam2Img.bmp");
            // M50 frame image + the legacy OCR form of the SAME part (underscore layout).
            var m50 = Path.Combine(input, $"{szid.PadRight(22, '0')}-{new string('0', 32)}-1-M50_ST040_KF1-1-&Cam1Img.bmp");
            var ocr = Path.Combine(input, "270726161219_00320440000000000000_1_M50_ST040_KF1_2_OCR_&Cam2Img_Dark.png");
            // A different part's trimmer that shares the first 12 chars — must survive.
            var neighbour = Path.Combine(input, $"{"2607270001552".PadRight(32, '0')}-{new string('0', 22)}-1-M20_ST060_KF3-1-&Cam1Img.bmp");
            foreach (var f in new[] { m2xCam1, m2xCam2, m50, ocr, neighbour })
                File.WriteAllText(f, "x");

            var result = new ImageHandler(new NullLog())
                .ApplyAsync("DE", new[] { szid, trimmer }, input, PartImageAction.Delete,
                    string.Empty, CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertEqual("frame + legacy-OCR + both M2X images deleted", 4, result.Handled);
            AssertTrue("M2X camera 1 (swapped widths) found", !File.Exists(m2xCam1));
            AssertTrue("M2X camera 2 (spec widths) found", !File.Exists(m2xCam2));
            AssertTrue("M50 frame image found", !File.Exists(m50));
            AssertTrue("legacy OCR image found via the reassembled serial", !File.Exists(ocr));
            AssertTrue("neighbouring trimmer survived (13th char differs)", File.Exists(neighbour));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- Target concept 2026-07-28: part-exit image actions, MSA move, deprecated keys ------------

    /// <summary>Build a spec-form filename (Serial1 22 / Serial2 32).</summary>
    private static string ImgName(string serial, string controller, string overall = "1", string cam = "1",
        string? dmc = null, string ext = ".bmp") =>
        $"{serial.PadRight(22, '0')}-{(dmc ?? string.Empty).PadRight(32, '0')}-{overall}-{controller}-{cam}-&Cam1Img{ext}";

    private static void PartExit_ImageActionsFollowTheTargetConcept()
    {
        Console.WriteLine("[Case AK] Part exit: NG/DE/Unknown delete in 01 ONLY; 03/04/05 are never touched");

        SerialNumberHelper.Configure(19);
        SerialNumberHelper.ConfigureTrimmer(13);
        const string szid = "2807260753160032218";

        var root = Path.Combine(Path.GetTempPath(), "hds_soll_" + Guid.NewGuid().ToString("N"));
        var lowRes = Path.Combine(root, "01_Low_Resolution_Individual", "Input");
        var ng = Path.Combine(root, "03_High_Resolution_NG", "Input");
        var diag = Path.Combine(root, "04_High_Resolution_Diagnostic", "Input");
        var gsm = Path.Combine(root, "05_High_Resolution_GoldenSample", "Input");
        var legacyDay = Path.Combine(root, "01_Low_Resolution_Individual", "2026", "07", "27");
        foreach (var d in new[] { lowRes, ng, diag, gsm, legacyDay })
            Directory.CreateDirectory(d);
        try
        {
            var inLowRes = Path.Combine(lowRes, ImgName(szid, "M50_ST130_KF1"));
            var inLegacy = Path.Combine(legacyDay, ImgName(szid, "M50_ST120_KF1"));       // legacy day-folder
            var inLowResPng = Path.Combine(lowRes, ImgName(szid, "M50_ST140_KF1", ext: ".png")); // no *.bmp filter
            var inNg = Path.Combine(ng, ImgName(szid, "M50_ST110_KF1", overall: "0"));
            var inDiag = Path.Combine(diag, ImgName(szid, "M50_ST040_KF1"));
            var inGsm = Path.Combine(gsm, ImgName(szid, "M10_ST060_KF1", cam: "4", ext: ".png"));
            foreach (var f in new[] { inLowRes, inLegacy, inLowResPng, inNg, inDiag, inGsm })
                File.WriteAllText(f, "x");

            var result = new ImageHandler(new NullLog())
                .ApplyAsync("NG", new[] { szid }, lowRes, PartImageAction.Delete, string.Empty, CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertEqual("all three low-res images deleted (Input + legacy day-folder, .bmp + .png)", 3, result.Handled);
            AssertTrue("low-res Input image gone", !File.Exists(inLowRes));
            AssertTrue("low-res legacy day-folder image gone", !File.Exists(inLegacy));
            AssertTrue("low-res .png gone (no extension filter any more)", !File.Exists(inLowResPng));
            AssertTrue("03_NG untouched", File.Exists(inNg));
            AssertTrue("04_Diagnostic untouched", File.Exists(inDiag));
            AssertTrue("05_GoldenSample untouched", File.Exists(inGsm));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }
    }

    private static void PartExit_OkFollowsCollageGenerateOnly()
    {
        Console.WriteLine("[Case AL] OK part: collage on → delete originals · collage off → move to backup · DeletePictures ignored");

        SerialNumberHelper.Configure(19);
        const string szid = "2807260900110032307";

        var root = Path.Combine(Path.GetTempPath(), "hds_ok_" + Guid.NewGuid().ToString("N"));
        var lowRes = Path.Combine(root, "01", "Input");
        var collageOut = Path.Combine(root, "02", "Input");
        var backup = Path.Combine(root, "06_Backup");
        Directory.CreateDirectory(lowRes);
        Directory.CreateDirectory(collageOut);
        try
        {
            var telegram = string.Join(";",
                "DMC1", szid, "", "1117", "Normal", "50", "1", "20", "2", "30", "3", "4", "21.5", "44.0", "OK");
            var part = SpsPartExitData.TryParse(telegram)!;

            // --- a) Collage OFF (live setting): the originals must be MOVED to the backup tree ---
            var img = Path.Combine(lowRes, ImgName(szid, "M50_ST130_KF1"));
            File.WriteAllText(img, "content");

            var cfgOff = new AppConfig
            {
                Nas = new NasConfig { LowResIndividualPath = lowRes, BackupFolder = backup },
                Collage = new CollageConfig { Generate = false, SingleImagesPath = lowRes, ResultImagesPath = collageOut },
            };
            var logOff = new RecordingLog();
            var collageOff = new RecordingCollage();
            var spsOff = new HandlerCaptureSps();
            var orchOff = new PartExitOrchestrator(spsOff, new ThrowingDb(), new RecordingCsv(), collageOff,
                new ImageHandler(logOff), new StubConfig2(cfgOff), new NoopHealth(), logOff);
            orchOff.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            spsOff.PartExitHandler!(part).GetAwaiter().GetResult();

            AssertTrue("collage off → no collage composed", !collageOff.ComposeCalled);
            AssertTrue("collage off → original removed from the low-res folder", !File.Exists(img));
            var moved = Directory.Exists(backup)
                ? Directory.GetFiles(backup, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
            AssertEqual("collage off → exactly one file in the backup tree", 1, moved.Length);
            AssertEqual("backup keeps the original name", Path.GetFileName(img), Path.GetFileName(moved[0]));
            AssertEqual("backup content intact", "content", File.ReadAllText(moved[0]));
            AssertTrue("backup uses the YYYY\\MM\\DD layout",
                moved[0].Contains(Path.Combine(DateTime.Now.ToString("yyyy"), DateTime.Now.ToString("MM"), DateTime.Now.ToString("dd"))));

            // --- b) Collage ON: the collage is composed and the originals are DELETED (no backup) ---
            var img2 = Path.Combine(lowRes, ImgName(szid, "M50_ST120_KF1"));
            File.WriteAllText(img2, "content2");
            var backupBefore = Directory.GetFiles(backup, "*", SearchOption.AllDirectories).Length;

            var cfgOn = new AppConfig
            {
                Nas = new NasConfig { LowResIndividualPath = lowRes, BackupFolder = backup },
                Collage = new CollageConfig { Generate = true, SingleImagesPath = lowRes, ResultImagesPath = collageOut },
            };
            var logOn = new RecordingLog();
            var collageOn = new RecordingCollage();
            var spsOn = new HandlerCaptureSps();
            var orchOn = new PartExitOrchestrator(spsOn, new ThrowingDb(), new RecordingCsv(), collageOn,
                new ImageHandler(logOn), new StubConfig2(cfgOn), new NoopHealth(), logOn);
            orchOn.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            spsOn.PartExitHandler!(part).GetAwaiter().GetResult();

            AssertTrue("collage on → collage composed", collageOn.ComposeCalled);
            AssertTrue("collage on → original deleted", !File.Exists(img2));
            AssertEqual("collage on → nothing added to the backup tree", backupBefore,
                Directory.GetFiles(backup, "*", SearchOption.AllDirectories).Length);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }
    }

    private static void PartExit_UnknownResult_NoCsvButDbImagesAndWarning()
    {
        Console.WriteLine("[Case AM] Unknown result: dmcserial yes, CSV NO, images deleted, WARNING with the raw field");

        SerialNumberHelper.Configure(19);
        const string szid = "2807260755460032238";

        var root = Path.Combine(Path.GetTempPath(), "hds_unk_" + Guid.NewGuid().ToString("N"));
        var lowRes = Path.Combine(root, "01", "Input");
        Directory.CreateDirectory(lowRes);
        try
        {
            var img = Path.Combine(lowRes, ImgName(szid, "M50_ST130_KF1"));
            File.WriteAllText(img, "x");

            // Field 14 is neither OK/NG/DE.
            var part = SpsPartExitData.TryParse(string.Join(";",
                "DMC1", szid, "", "1117", "Normal", "50", "1", "20", "2", "30", "3", "4", "1.0", "2.0", "WEIRD"))!;
            AssertEqual("parses as Unknown", PartResult.Unknown, part.Result);
            AssertEqual("raw field 14 kept", "WEIRD", part.ResultRaw);

            var cfg = new AppConfig
            {
                Nas = new NasConfig { LowResIndividualPath = lowRes },
                Collage = new CollageConfig { SingleImagesPath = lowRes },
            };
            var log = new RecordingLog();
            var csv = new RecordingCsv();
            var db = new ThrowingDb();   // flags the dmcserial attempt (and refuses it)
            var sps = new HandlerCaptureSps();
            var orch = new PartExitOrchestrator(sps, db, csv, new RecordingCollage(),
                new ImageHandler(log), new StubConfig2(cfg), new NoopHealth(), log);
            orch.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            sps.PartExitHandler!(part).GetAwaiter().GetResult();

            AssertTrue("dmcserial write attempted (like NG)", db.Opened);
            AssertTrue("NO CSV row for an unknown result", !csv.WriteCalled);
            AssertTrue("low-res image deleted (like NG)", !File.Exists(img));
            AssertTrue("WARNING names the raw field and the raw telegram",
                log.Messages.Any(m => m.Contains("unknown result") && m.Contains("WEIRD") && m.Contains("Raw telegram")));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }
    }

    private static void PartExit_EmptySzid_WritesNoDmcserialRow()
    {
        Console.WriteLine("[Case AN] Part exit without a frame serial: no dmcserial row (no '' key collision), WARNING instead");

        SerialNumberHelper.Configure(19);
        var root = Path.Combine(Path.GetTempPath(), "hds_nos_" + Guid.NewGuid().ToString("N"));
        var lowRes = Path.Combine(root, "01", "Input");
        Directory.CreateDirectory(lowRes);
        try
        {
            // Block-ejected part: no SZID, no DMC, no trimmer (this really happens on the line).
            var part = SpsPartExitData.TryParse(string.Join(";",
                "", "", "", "1117", "Normal", "50", "1", "20", "2", "30", "3", "4", "1.0", "2.0", "NG"))!;

            var cfg = new AppConfig
            {
                Nas = new NasConfig { LowResIndividualPath = lowRes },
                Collage = new CollageConfig { SingleImagesPath = lowRes },
            };
            var log = new RecordingLog();
            var db = new ThrowingDb();
            var sps = new HandlerCaptureSps();
            var orch = new PartExitOrchestrator(sps, db, new RecordingCsv(), new RecordingCollage(),
                new ImageHandler(log), new StubConfig2(cfg), new NoopHealth(), log);
            orch.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var outcome = sps.PartExitHandler!(part).GetAwaiter().GetResult();

            AssertTrue("DB never opened → no serial_number = '' row", !db.Opened);
            AssertTrue("WARNING reports the serial-less part exit",
                log.Messages.Any(m => m.Contains("without a frame serial") && m.Contains("no dmcserial row")));
            AssertTrue("still a positive ACK (nothing the PLC can fix)", outcome.Success);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }
    }

    private static void DeprecatedIniKeys_AreReportedNotSilentlyHonoured()
    {
        Console.WriteLine("[Case AO] Deprecated keys: [NAS] DeletePictures + [CSV] CSVMSA_Save are reported, not honoured");
        var ini = Path.Combine(Path.GetTempPath(), "hds_dep_" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(ini,
            "[NAS]\r\nDeletePictures=true\r\nBackupFolder=Z:\\06_Backup\r\n\r\n" +
            "[CSV]\r\nCSVMSA_Save=true\r\nCSV_MSAPath=Y:\\01_CSV_Evaluation\r\n\r\n" +
            "[Retention]\r\nImages_GoldenSample=3\r\n");
        try
        {
            var cfg = new IniConfigManager().Load(ini);

            AssertTrue("[NAS] DeletePictures reported as deprecated + ignored",
                cfg.Retention.Deprecations.Any(d => d.Contains("DeletePictures") && d.Contains("IGNORED")));
            AssertTrue("[CSV] CSVMSA_Save reported as deprecated + ignored",
                cfg.Retention.Deprecations.Any(d => d.Contains("CSVMSA_Save") && d.Contains("IGNORED")));
            AssertTrue("CSV_MSAPath is NOT flagged (still the retention root of the legacy tree)",
                !cfg.Retention.Deprecations.Any(d => d.Contains("CSV_MSAPath")));
            AssertEqual("CSV_MSAPath still readable for retention", @"Y:\01_CSV_Evaluation", cfg.Csv.MsaPath);
            AssertEqual("Images_GoldenSample = 3 (transit buffer)", 3, cfg.Retention.ImagesGoldenSample);
        }
        finally { try { File.Delete(ini); } catch { /* best effort */ } }
    }

    private static void PartExitAck_StaysByteIdentical_DespiteTheMsDisplay()
    {
        Console.WriteLine("[Case AP] The ms suffix is display-only — the ACK telegram to the PLC is byte-identical");

        // The wire format is <SZID padded to 32>;true|false + CR — the duration lives in a separate
        // display string (TcpSpsServer.HandlePartExitAckAsync), so it can never reach the PLC.
        const string szid = "2807261253010038778";
        var outcome = new PartExitOutcome(true, 87);
        var body = $"{szid.PadRight(32, '0')};{(outcome.Success ? "true" : "false")}";
        var wire = body + "\r";
        var display = $"{body} ({outcome.DurationMs} ms)";

        AssertEqual("wire length = 32 serial + ';' + 'true' + CR", 32 + 1 + 4 + 1, wire.Length);
        AssertTrue("wire ends with a single CR", wire.EndsWith("\r") && !wire.EndsWith("\n"));
        AssertTrue("wire carries no 'ms' text", !wire.Contains("ms"));
        AssertTrue("wire carries no parenthesis", !wire.Contains('(') && !wire.Contains(')'));
        AssertEqual("display = wire body + duration", $"{body} (87 ms)", display);
        AssertTrue("display starts with the untouched wire body", display.StartsWith(body));
    }

    private static void MsaRunImages_AreMovedNotCopied()
    {
        Console.WriteLine("[Case AQ] MSA run images are MOVED out of the GoldenSample transit folder (failures stay behind)");

        const string baseId = "50260723165426";
        const string dmc = "21072615261304011996000000035951";   // real live DMC → Serial2 != zeros

        var root = Path.Combine(Path.GetTempPath(), "hds_msamove_" + Guid.NewGuid().ToString("N"));
        var gsm = Path.Combine(root, "05_High_Resolution_GoldenSample", "Input");
        var imgDir = Path.Combine(root, "X_MSA_Reports", baseId, "IMG");
        Directory.CreateDirectory(gsm);
        try
        {
            // Two images of the run (BaseID + loop in Serial1, DMC in Serial2) …
            var run1 = Path.Combine(gsm, ImgName(baseId + "001", "M50_ST040_KF1", overall: "0", dmc: dmc, ext: ".png"));
            var run2 = Path.Combine(gsm, ImgName(baseId + "002", "M50_ST130_KF1", overall: "0", dmc: dmc, ext: ".png"));
            // … a different run and an M1X production image, both must stay.
            var otherRun = Path.Combine(gsm, ImgName("50260724070500001", "M50_ST040_KF1", dmc: dmc, ext: ".png"));
            var m1xProd = Path.Combine(gsm, ImgName("2707261558510031730", "M10_ST060_KF1", cam: "4", ext: ".png"));
            foreach (var f in new[] { run1, run2, otherRun, m1xProd })
                File.WriteAllText(f, "img");

            var log = new RecordingLog();
            var result = MsaRunImages.Move(gsm, baseId, imgDir, log);

            AssertEqual("both run images found", 2, result.Found);
            AssertEqual("both run images moved", 2, result.Moved);
            AssertEqual("nothing left behind", 0, result.LeftBehind);
            AssertTrue("run image 1 arrived in IMG", File.Exists(Path.Combine(imgDir, Path.GetFileName(run1))));
            AssertTrue("run image 2 arrived in IMG", File.Exists(Path.Combine(imgDir, Path.GetFileName(run2))));
            AssertTrue("run image 1 gone from the transit folder (moved, not copied)", !File.Exists(run1));
            AssertTrue("run image 2 gone from the transit folder (moved, not copied)", !File.Exists(run2));
            AssertTrue("a different run's image stays", File.Exists(otherRun));
            AssertTrue("M1X production image stays", File.Exists(m1xProd));
            AssertTrue("log says 'moved', not 'copied'",
                log.Messages.Any(m => m.Contains("moved into") && !m.Contains("copied")));

            // A failing move must leave the original in place (here: the target name is blocked by a
            // read-only file, so Copy throws).
            var blocked = Path.Combine(gsm, ImgName(baseId + "003", "M50_ST120_KF1", overall: "0", dmc: dmc, ext: ".png"));
            File.WriteAllText(blocked, "img");
            var blockedDest = Path.Combine(imgDir, Path.GetFileName(blocked));
            File.WriteAllText(blockedDest, "locked");
            File.SetAttributes(blockedDest, FileAttributes.ReadOnly);
            try
            {
                var second = MsaRunImages.Move(gsm, baseId, imgDir, log);
                AssertEqual("the blocked image was found", 1, second.Found);
                AssertEqual("it was NOT moved", 0, second.Moved);
                AssertEqual("it is reported as left behind", 1, second.LeftBehind);
                AssertTrue("original still in the transit folder after a failed move", File.Exists(blocked));
                AssertTrue("WARNING says the original was left in place",
                    log.Messages.Any(m => m.Contains("could not be moved") && m.Contains("left in place")));
            }
            finally { File.SetAttributes(blockedDest, FileAttributes.Normal); }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }
    }

    // ---- View mechanics: console tail-follow (log tab + PLC channel cards) -----------------------

    /// <summary>
    /// Verifies <see cref="TailScrollView"/> headlessly: the control + a virtualizing ListBox are laid
    /// out off-screen (Measure/Arrange builds the template and the ScrollViewer reports real offsets),
    /// then the required interactions are driven programmatically. No window is shown and no service is
    /// started, so this never touches the live plant.
    /// </summary>
    private static void TailScroll_FollowsPausesCountsAndResumes()
    {
        Console.WriteLine("[Case AR] TailScrollView: follows the tail, pauses on scroll-up/click, counts, resumes");

        // The log tab rebuilds its collection every tick with NEW item objects — reproduce exactly that,
        // including the ring buffer that drops the oldest entry, because that is what breaks naive
        // offset restoring.
        const int ring = 40;
        var backing = new List<string>();
        var items = new ObservableCollection<LogEntryVm>();
        var seq = 0;

        void Append(int n)
        {
            for (var i = 0; i < n; i++)
            {
                backing.Add($"line {++seq:0000}");
                while (backing.Count > ring)
                    backing.RemoveAt(0);
            }
            items.Clear();                                  // per-tick rebuild, like LogViewModel.Tick
            foreach (var text in backing)
                items.Add(new LogEntryVm { Text = text, Brush = Brushes.White });
        }

        var list = new ListBox
        {
            ItemsSource = items,
            ItemTemplate = LineTemplate(),
        };
        var host = new TailScrollView { Content = list, Width = 300, Height = 100 };

        // Give the control the theme template that carries PART_JumpButton (App.xaml is not running).
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/HarryDataServer;component/Themes/DarkTheme.xaml", UriKind.Relative),
        });

        Append(60);          // more than fits → the list is scrollable
        Layout(host);

        var sv = FindScrollViewer(host);
        AssertTrue("ScrollViewer found in the laid-out tree", sv is not null);
        AssertTrue("list is scrollable (more items than fit)", sv!.ScrollableHeight > 0);

        // 1) Starts at the tail and follows.
        AssertTrue("starts at the bottom", sv.VerticalOffset >= sv.ScrollableHeight - 2.0);
        AssertTrue("not paused initially", !host.IsPaused);
        Append(5);
        Layout(host);
        AssertTrue("still at the bottom after new entries (following)", sv.VerticalOffset >= sv.ScrollableHeight - 2.0);
        AssertEqual("no counter while following", 0, host.NewCount);

        // 2) User scrolls up → pauses and the view holds its position while entries keep coming.
        //    The ring drops the oldest rows on every append, so holding a raw offset is NOT enough —
        //    the same LINE has to stay under the viewport.
        sv.ScrollToVerticalOffset(20);
        Layout(host);
        AssertTrue("paused after scrolling up", host.IsPaused);
        AssertTrue("no jump button before anything new arrived", !host.IsJumpAvailable);

        var topTextBefore = ((LogEntryVm)list.Items[(int)sv.VerticalOffset]).Text;
        Append(7);
        Layout(host);
        AssertEqual("view still shows the SAME line although the ring dropped 7 entries",
            topTextBefore, ((LogEntryVm)list.Items[(int)sv.VerticalOffset]).Text);
        AssertTrue("…and the raw offset really did move to follow it", sv.VerticalOffset < 20);

        // 3) The overlay counts up.
        AssertEqual("counter shows the 7 new entries", 7, host.NewCount);
        AssertTrue("jump button offered", host.IsJumpAvailable);
        Append(3);
        Layout(host);
        AssertEqual("counter keeps counting", 10, host.NewCount);
        AssertTrue("still paused while entries stream in", host.IsPaused);
        AssertEqual("and still the same line on top", topTextBefore,
            ((LogEntryVm)list.Items[(int)sv.VerticalOffset]).Text);

        // 3a) Anchor pushed out of the ring: degrade gracefully — stay paused, keep counting, no crash.
        Append(ring + 5);
        Layout(host);
        AssertTrue("still paused after the anchor fell out of the ring", host.IsPaused);
        AssertEqual("counter reports every entry now in the list as new", list.Items.Count, host.NewCount);
        AssertTrue("jump button still offered", host.IsJumpAvailable);

        // 3b) Clicking it jumps to the end and resumes.
        host.ScrollToEnd();
        Layout(host);
        AssertTrue("jumped to the bottom", sv.VerticalOffset >= sv.ScrollableHeight - 2.0);
        AssertTrue("following again", !host.IsPaused);
        AssertEqual("counter cleared", 0, host.NewCount);
        AssertTrue("jump button hidden again", !host.IsJumpAvailable);

        // 4) Manual scroll back to the bottom re-arms following (tolerance included).
        sv.ScrollToVerticalOffset(3);
        Layout(host);
        AssertTrue("paused again", host.IsPaused);
        sv.ScrollToVerticalOffset(sv.ScrollableHeight - 1.0);   // within tolerance, not exactly the end
        Layout(host);
        AssertTrue("scrolling (almost) to the bottom resumes following", !host.IsPaused);
        Append(2);
        Layout(host);
        AssertTrue("and it really follows again", sv.VerticalOffset >= sv.ScrollableHeight - 2.0);

        // 5) Clicking into the list pauses — this is what makes copying a line possible while the log runs.
        host.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            Source = list,
        });
        AssertTrue("clicking into the list pauses following", host.IsPaused);

        var offsetAtClick = sv.VerticalOffset;
        Append(4);
        Layout(host);
        AssertTrue("view does not move while the mouse button is still down",
            Math.Abs(sv.VerticalOffset - offsetAtClick) < 0.001);
        AssertEqual("new entries are counted for the operator", 4, host.NewCount);

        host.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
            Source = list,
        });
        AssertTrue("still paused after releasing the button (position kept)", host.IsPaused);

        // 6) Copying a line while the log keeps running: the selection must survive the rebuild that
        //    replaces every item object (otherwise it silently disappears within a second).
        list.SelectedIndex = list.Items.Count - 1;   // the operator picks the line they want to copy
        var selectedText = ((LogEntryVm)list.SelectedItem!).Text;
        Append(3);
        Layout(host);
        AssertTrue("a line stays selected across the per-tick rebuild", list.SelectedItem is not null);
        AssertEqual("and it is still the SAME line", selectedText, ((LogEntryVm)list.SelectedItem!).Text);
        AssertEqual("the copy affordance reports that exact line", selectedText, host.SelectedLineText());

        Append(ring + 2);   // push the selected line out of the ring
        Layout(host);
        AssertTrue("selection is dropped (not restored to a wrong line) once the line is gone",
            host.SelectedLineText() != selectedText);
    }

    private static DataTemplate LineTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text"));
        return new DataTemplate(typeof(LogEntryVm)) { VisualTree = text };
    }

    /// <summary>Force a full off-screen layout pass and drain the dispatcher queue (no window shown).</summary>
    private static void Layout(FrameworkElement element)
    {
        for (var i = 0; i < 3; i++)
        {
            element.Measure(new Size(element.Width, element.Height));
            element.Arrange(new Rect(0, 0, element.Width, element.Height));
            element.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                return sv;
            if (FindScrollViewer(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    private static void BackupRoot_NonInputPathIsItsOwnRoot()
    {
        Console.WriteLine("[Case AB] Backup folder (no \\Input) is its own sorted root for retention");
        AssertEqual("non-Input path → itself", @"Z:\06_Backup", ImageFileName.SortedRoot(@"Z:\06_Backup"));
        AssertEqual("Input path → parent", @"Z:\01_Low_Resolution_Individual",
            ImageFileName.SortedRoot(@"Z:\01_Low_Resolution_Individual\Input"));
    }

    private static void Retention_NewSectionWins_And_ZeroMeansNever()
    {
        Console.WriteLine("[Case AD] [Retention] section: values applied, 0 = never, defaults for absent keys");
        var ini = Path.Combine(Path.GetTempPath(), "hds_ret_" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(ini,
            "[Retention]\r\n" +
            "Images_NG=10\r\n" +
            "Images_InputLeftovers=2\r\n" +
            "Database_MSA=0\r\n" +
            "Reports_MSA=0\r\n" +
            "Database_Production=42\r\n");
        try
        {
            var ret = new IniConfigManager().Load(ini).Retention;
            AssertEqual("Images_NG applied", 10, ret.ImagesNg);
            AssertEqual("Images_InputLeftovers applied", 2, ret.ImagesInputLeftovers);
            AssertEqual("Database_MSA = 0 (never)", 0, ret.DatabaseMsa);
            AssertEqual("Reports_MSA = 0 (never)", 0, ret.ReportsMsa);
            AssertEqual("Database_Production applied", 42, ret.DatabaseProduction);
            AssertEqual("CSV_Merge default 365", 365, ret.CsvMerge);
            AssertEqual("Images_Collage default 30", 30, ret.ImagesCollage);
            AssertEqual("no deprecations when [Retention] is used", 0, ret.Deprecations.Count);
        }
        finally { try { File.Delete(ini); } catch { /* best effort */ } }
    }

    private static void Retention_LegacyKeysFallBack_WithDeprecation()
    {
        Console.WriteLine("[Case AE] Legacy retention keys fall back into [Retention] with a deprecation WARNING");
        var ini = Path.Combine(Path.GetTempPath(), "hds_retlegacy_" + Guid.NewGuid().ToString("N") + ".ini");
        // No [Retention] section: values must come from the legacy [MySQL]/[NAS] keys.
        File.WriteAllText(ini,
            "[MySQL]\r\nRetentionPeriodDays=50\r\n\r\n" +
            "[NAS]\r\nRetentionNGDays=20\r\nBackupRetentionDays=7\r\n");
        try
        {
            var ret = new IniConfigManager().Load(ini).Retention;
            AssertEqual("Database_Production from legacy RetentionPeriodDays", 50, ret.DatabaseProduction);
            AssertEqual("Images_NG from legacy RetentionNGDays", 20, ret.ImagesNg);
            AssertEqual("Images_Backup from legacy BackupRetentionDays", 7, ret.ImagesBackup);
            AssertTrue("deprecation recorded for Database_Production",
                ret.Deprecations.Any(d => d.Contains("RetentionPeriodDays") && d.Contains("Database_Production")));
            AssertTrue("deprecation recorded for Images_NG",
                ret.Deprecations.Any(d => d.Contains("RetentionNGDays") && d.Contains("Images_NG")));
        }
        finally { try { File.Delete(ini); } catch { /* best effort */ } }
    }

    private static void DePartExit_NoDbNoCsv_DeletesImages_Logs()
    {
        Console.WriteLine("[Case AC] DE part exit: NO dmcserial, NO CSV, images deleted, INFO logged");
        var root = Path.Combine(Path.GetTempPath(), "hds_dep_" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "Input");
        Directory.CreateDirectory(input);
        try
        {
            const string trimmer = "2607230000810";
            var img = Path.Combine(input, $"{trimmer}-000-1-M20_ST060_KF1-1-&Cam1.bmp");
            File.WriteAllText(img, "x");

            SerialNumberHelper.ConfigureTrimmer(13);
            var cfg = new AppConfig { Nas = new NasConfig { LowResIndividualPath = input } };
            var db = new ThrowingDb();     // must never be opened for DE
            var csv = new RecordingCsv();  // WritePartAsync must never be called for DE
            var collage = new RecordingCollage();
            var sps = new HandlerCaptureSps();
            var log = new RecordingLog();

            var orch = new PartExitOrchestrator(sps, db, csv, collage,
                new ImageHandler(log), new StubConfig2(cfg), new NoopHealth(), log);
            orch.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            AssertTrue("orchestrator registered a part-exit handler", sps.PartExitHandler is not null);

            var de = SpsPartExitData.TryParse(string.Join(";",
                "", "", trimmer, "1117", "Normal", "20", "2", "20", "2", "30", "3", "4", "1.0", "2.0", "DE"))!;
            var outcome = sps.PartExitHandler!(de).GetAwaiter().GetResult();

            AssertTrue("ACK success", outcome.Success);
            AssertTrue("duration reported for the UI line", outcome.DurationMs >= 0);
            AssertTrue("no dmcserial write (DB never opened)", !db.Opened);
            AssertTrue("no CSV row written", !csv.WriteCalled);
            AssertTrue("no collage composed", !collage.ComposeCalled);
            AssertTrue("trimmer image deleted", !File.Exists(img));
            AssertTrue("INFO 'DE: … deleted' logged",
                log.Messages.Any(m => m.Contains("DE") && m.Contains("deleted")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Minimal IConfigService stub for the PDF-name test (never reads config when OutputDirectory is set).</summary>
    private sealed class StubConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public string IniPath => string.Empty;
        public AppConfig Reload() => Config;
    }

    // ---- Stubs for the DE part-exit orchestrator test (Case AC) ----

    private sealed class StubConfig2 : IConfigService
    {
        private readonly AppConfig _cfg;
        public StubConfig2(AppConfig cfg) => _cfg = cfg;
        public AppConfig Config => _cfg;
        public string IniPath => string.Empty;
        public AppConfig Reload() => _cfg;
    }

    /// <summary>DB stub that flags (and refuses) any connection — proves DE never writes dmcserial.</summary>
    private sealed class ThrowingDb : IDatabaseService
    {
        public bool Opened { get; private set; }
        public DatabaseStatus Status => DatabaseStatus.Ready;
        public event Action<DatabaseStatus>? StatusChanged { add { } remove { } }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<MySqlConnector.MySqlConnection> OpenConnectionAsync(CancellationToken ct = default)
        {
            Opened = true;
            throw new InvalidOperationException("DB must not be touched for a DE part exit");
        }
        public Task<IReadOnlyDictionary<string, long>> GetRowCountsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
        public Task<ProductionSnapshot> GetProductionSnapshotAsync(CancellationToken ct = default)
            => Task.FromResult(new ProductionSnapshot(0, 0, null, string.Empty));
        public Task<MySqlServerStatus> GetServerStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new MySqlServerStatus(false, 0, TimeSpan.Zero));
    }

    private sealed class RecordingCsv : ICsvService
    {
        public bool WriteCalled { get; private set; }
        public int PendingCount => 0;
        public long TotalRows => 0;
        public string? ActiveFilePath => null;
        public DateTime? LastWriteTime => null;
        public event Action? StatsChanged { add { } remove { } }
        public Task<bool> WritePartAsync(SpsPartExitData part, CancellationToken ct = default)
        {
            WriteCalled = true;
            return Task.FromResult(true);
        }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class RecordingCollage : ICollageService
    {
        public bool ComposeCalled { get; private set; }
        public int PendingCount => 0;
        public long TotalGenerated => 0;
        public event Action? StatsChanged { add { } remove { } }
        public event Action<string, DateTime>? CollageGenerated { add { } remove { } }
        public Task<bool> ComposeForPartAsync(SpsPartExitData part, CancellationToken ct)
        {
            ComposeCalled = true;
            return Task.FromResult(true);
        }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    /// <summary>SPS stub that just captures the part-exit handler the orchestrator registers.</summary>
    private sealed class HandlerCaptureSps : ISpsServer
    {
        public bool IsRunning => false;
        public int ListeningChannels => 0;
        public int ActiveConnections => 0;
        public event Action? StatusChanged { add { } remove { } }
        public event EventHandler<SpsPartExitEventArgs>? PartExitReceived { add { } remove { } }
        public event Action<SpsChannel, bool, string>? ChannelActivity { add { } remove { } }
        public int ConnectionsOn(SpsChannel channel) => 0;
        public Func<string, string, string>? MsaRequestHandler { get; set; }
        public Task<bool> PushMsaResultAsync(string moduleKey, string baseId, string status, CancellationToken ct = default)
            => Task.FromResult(true);
        public Func<SpsPartExitData, Task<PartExitOutcome>>? PartExitHandler { get; set; }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class NoopHealth : ISystemHealth
    {
        public void Report(string source, HealthSeverity severity, string message, TimeSpan? ttl = null) { }
        public void Clear(string source) { }
        public HealthSnapshot Snapshot() => new(null, "OK", string.Empty, Array.Empty<HealthFault>());
        public event Action? Changed { add { } remove { } }
    }

    /// <summary>Log that renders {placeholder} messages to text so assertions can match the final line.</summary>
    private sealed class RecordingLog : ILogService
    {
        public List<string> Messages { get; } = new();
        private void Add(string message, object?[] p)
        {
            var text = message;
            var i = 0;
            while (i < p.Length)
            {
                var open = text.IndexOf('{');
                if (open < 0) break;
                var close = text.IndexOf('}', open);
                if (close < 0) break;
                text = text[..open] + (p[i]?.ToString() ?? string.Empty) + text[(close + 1)..];
                i++;
            }
            Messages.Add(text);
        }
        public void Debug(string message, params object?[] p) => Add(message, p);
        public void Information(string message, params object?[] p) => Add(message, p);
        public void Warning(string message, params object?[] p) => Add(message, p);
        public void Error(string message, params object?[] p) => Add(message, p);
        public void Error(Exception exception, string message, params object?[] p) => Add(message, p);
        public void Shutdown() { }
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
