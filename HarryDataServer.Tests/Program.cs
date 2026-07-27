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
            // sorted YYYY\MM\DD day-folder are searched. Delete by BOTH the frame and the trimmer serial.
            var deleted = handler
                .DeleteBySerialsAsync(new[] { szid, trimmer }, new[] { input }, CancellationToken.None)
                .GetAwaiter().GetResult();

            AssertEqual("deleted frame + trimmer image", 2, deleted);
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
            var ack = sps.PartExitHandler!(de).GetAwaiter().GetResult();

            AssertTrue("ACK success", ack);
            AssertTrue("no dmcserial write (DB never opened)", !db.Opened);
            AssertTrue("no CSV row written", !csv.WriteCalled);
            AssertTrue("no collage composed", !collage.ComposeCalled);
            AssertTrue("trimmer image deleted", !File.Exists(img));
            AssertTrue("INFO 'DE: … deleted' logged",
                log.Messages.Any(m => m.Contains("DE:") && m.Contains("deleted")));
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
        public Func<SpsPartExitData, Task<bool>>? PartExitHandler { get; set; }
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
