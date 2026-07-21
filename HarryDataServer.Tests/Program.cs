using System.Globalization;
using System.IO;
using HarryDataServer.Communication;
using HarryDataServer.Configuration;
using HarryDataServer.Models;
using HarryDataServer.Services;

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
