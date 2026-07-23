using System.Globalization;
using System.IO;
using HarryDataServer.Configuration;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using LimitSampleReference = HarryShared.Data.LimitSampleReference;

namespace HarryDataServer.Services;

/// <summary>
/// Renders the MSA PDF reports (SOW §3.2.1) with QuestPDF. Two reports per run:
/// one listing all measurements, one listing only the failed measurements. Files
/// are named <c>&lt;Module&gt;_&lt;Type&gt;_&lt;DDMMYY_HHMMSS&gt;_AllResults.pdf</c> /
/// <c>_FailuresOnly.pdf</c> and written to the run's PDF subfolder
/// <c>[MSA] ResultPath\YYYY\MM\DD\&lt;BaseID&gt;\PDF</c> (see <see cref="MsaResultLayout"/>).
/// </summary>
public sealed class PdfReportService : IPdfReportService
{
    static PdfReportService()
    {
        // QuestPDF Community licence (free for this use); must be set before any render.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private readonly IConfigService _config;
    private readonly ILogService _log;

    public PdfReportService(IConfigService config, ILogService log)
    {
        _config = config;
        _log = log;
    }

    public MsaReportPaths ResolvePaths(MsaReportData report)
    {
        // Prefer the run root the MSA engine already resolved (report path with network fallback, task B/D).
        var runRoot = report.OutputDirectory;
        if (string.IsNullOrWhiteSpace(runRoot))
        {
            var msa = _config.Config.Msa;
            runRoot = MsaResultLayout.EnsureWritableReportDir(
                          msa.ReportPath, msa.ReportFallbackPath, msa.ResultPath, report.Module, report.BaseId, report.RunAt, _log)
                      ?? MsaResultLayout.ReportRunRoot(MsaResultLayout.DefaultReportFallback, report.Module, report.BaseId, report.RunAt);
        }
        // PDFs live in the run root's PDF\ subfolder (RAW\ and IMG\ sit beside it).
        var pdfDir = Path.Combine(runRoot, MsaResultLayout.PdfSubfolder);
        // Per-part reports (LimitSample/MSA1) carry the DMC in the file name (task B4).
        var dmcPart = string.IsNullOrWhiteSpace(report.Dmc) ? string.Empty : "_" + Sanitize(report.Dmc);
        var baseName = $"{Sanitize(report.Module)}_{Sanitize(report.TestType)}_{Sanitize(report.BaseId)}{dmcPart}_{FileNaming.Stamp(report.RunAt)}";
        return new MsaReportPaths(
            Path.Combine(pdfDir, baseName + "_AllResults.pdf"),
            Path.Combine(pdfDir, baseName + "_FailuresOnly.pdf"));
    }

    public MsaReportPaths Generate(MsaReportData report)
    {
        var paths = ResolvePaths(report);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.AllResults)!);

        // Fill the reference-file head from config when the report did not carry it (on-demand UI path, task 1).
        var (refFile, refMtime, legacyFile) = ResolveReferenceInfo(report);

        var failures = report.Rows.Where(r => !r.Passed).ToList();

        // LimitSample gets its own compact layout (task B); MSA1/MSA3 keep the numeric capability table.
        if (string.Equals(report.TestType, "LimitSample", StringComparison.OrdinalIgnoreCase))
        {
            BuildLimitSampleDocument(report, report.Rows, refFile, refMtime, legacyFile).GeneratePdf(paths.AllResults);
            BuildLimitSampleDocument(report, failures, refFile, refMtime, legacyFile).GeneratePdf(paths.FailuresOnly);
        }
        else
        {
            BuildDocument(report, report.Rows, refFile, refMtime, legacyFile).GeneratePdf(paths.AllResults);
            BuildDocument(report, failures, refFile, refMtime, legacyFile).GeneratePdf(paths.FailuresOnly);
        }

        _log.Information("MSA PDF reports written for {Module}/{Type} ({Failures} failure(s)): {All} | {Fail}",
            report.Module, report.TestType, failures.Count, paths.AllResults, paths.FailuresOnly);
        return paths;
    }

    private static string Fmt(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";

    /// <summary>
    /// Resolve the reference file shown in the head (task A2). For a per-part LimitSample report the
    /// file actually used is the per-DMC file <c>&lt;ReferencePath&gt;\&lt;Module&gt;\LimitSamples\&lt;DMC&gt;.json</c>
    /// (deterministic from config, so this is correct for the on-demand UI path too) — the misleading
    /// module-wide MSA_&lt;module&gt;.json is NEVER used as a silent fallback. The legacy path is returned
    /// separately and shown only when the run really used it (<see cref="MsaReportData.LegacyReferenceUsed"/>).
    /// </summary>
    private (string File, DateTime? Modified, string? LegacyFile) ResolveReferenceInfo(MsaReportData report)
    {
        var refFolder = _config.Config.Msa.ReferencePath;
        string? legacyFile = report.LegacyReferenceUsed
            ? MsaReferenceLoader.ReferenceFilePath(refFolder, report.Module)
            : null;

        // Per-part LimitSample: the reference is the per-DMC file (found → mtime, missing → NOT FOUND).
        if (string.Equals(report.TestType, "LimitSample", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(report.Dmc))
        {
            var perPart = LimitSampleReference.PathFor(refFolder, report.Module, report.Dmc);
            return (perPart, File.Exists(perPart) ? File.GetLastWriteTime(perPart) : null, legacyFile);
        }

        // Otherwise use exactly what the report carries (empty → "none found"); no legacy auto-fallback.
        var file = report.ReferenceFile;
        var mtime = report.ReferenceFileModified;
        if (mtime is null && !string.IsNullOrEmpty(file) && File.Exists(file))
            mtime = File.GetLastWriteTime(file);
        return (file, mtime, legacyFile);
    }

    private static IDocument BuildDocument(
        MsaReportData report, IReadOnlyList<MsaReportRow> rows, string referenceFile, DateTime? referenceModified,
        string? legacyReferenceFile) =>
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(8));

                page.Header().Column(col =>
                {
                    col.Item().Text($"MSA Report — {report.Module}  {report.TestType}")
                        .FontSize(15).SemiBold();
                    col.Item().PaddingTop(2).Text(t =>
                    {
                        t.Span("Run: ").SemiBold();
                        t.Span(report.RunAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        t.Span($"     Controller(s): {report.Controller}     BaseID: {report.BaseId}");
                    });
                    // Head context (task B3): parts, loops, time range, criterion, reference file.
                    col.Item().Text(t =>
                    {
                        t.Span("Parts (DMCs): ").SemiBold(); t.Span($"{report.PartCount}");
                        t.Span("   Loops: ").SemiBold(); t.Span($"{report.LoopCount}");
                        t.Span("   Period: ").SemiBold();
                        t.Span($"{report.FromTime:yyyy-MM-dd HH:mm:ss} … {report.ToTime:HH:mm:ss}");
                    });
                    col.Item().Text(t =>
                    {
                        t.Span("Criterion: ").SemiBold(); t.Span(string.IsNullOrEmpty(report.Criterion) ? "—" : report.Criterion);
                    });
                    col.Item().Text(t =>
                    {
                        t.Span("Reference file: ").SemiBold();
                        if (string.IsNullOrEmpty(referenceFile))
                        {
                            t.Span("(none found)");
                        }
                        else
                        {
                            t.Span(referenceFile);
                            t.Span(referenceModified is { } m
                                ? $"  (modified {m:yyyy-MM-dd HH:mm:ss})"
                                : "  (NOT FOUND)");
                        }
                    });
                    // Legacy module-wide reference is shown ONLY when it was really used as a fallback (task A2).
                    if (!string.IsNullOrEmpty(legacyReferenceFile))
                        col.Item().Text(t =>
                        {
                            t.Span("Legacy fallback reference: ").SemiBold();
                            t.Span($"{legacyReferenceFile} (used — no per-part reference)")
                                .FontColor(Colors.Orange.Darken2);
                        });
                    col.Item().PaddingTop(3).Text(t =>
                    {
                        t.Span("Overall result: ").SemiBold();
                        var (label, color) = report.Verdict switch
                        {
                            MsaVerdict.Pass => ("PASS", Colors.Green.Darken2),
                            MsaVerdict.Fail => ("FAIL", Colors.Red.Darken2),
                            _ => ("INVALID", Colors.Orange.Darken2),
                        };
                        t.Span(label).Bold().FontColor(color);
                        if (!string.IsNullOrEmpty(report.VerdictReason))
                            t.Span($"  —  {report.VerdictReason}").FontColor(color);
                    });
                    // Cameras that produced no real judgement in the run (task 4).
                    foreach (var warning in report.ControllerWarnings)
                        col.Item().Text(t => t.Span("⚠ " + warning).FontColor(Colors.Orange.Darken2).SemiBold());
                    // Informational notes (e.g. taught parts missing from the run, task A3).
                    foreach (var note in report.Notes)
                        col.Item().Text(t => t.Span("• " + note).FontColor(Colors.Grey.Darken1));
                    col.Item().PaddingTop(3).LineHorizontal(1).LineColor(Colors.Grey.Medium);
                });

                page.Content().PaddingVertical(6).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(2.2f); // Controller
                        c.RelativeColumn(2.6f); // DMC (part)
                        c.RelativeColumn(3);    // Measurement
                        c.RelativeColumn(0.7f); // n
                        c.RelativeColumn(1.2f); // Mean
                        c.RelativeColumn(1.2f); // StdDev
                        c.RelativeColumn(1.1f); // Reference
                        c.RelativeColumn(1.1f); // Tolerance
                        c.RelativeColumn(2.2f); // Cg/Cgk or %P/T
                        c.RelativeColumn(1.0f); // Result
                        c.RelativeColumn(3.4f); // Reason / notes
                    });

                    table.Header(header =>
                    {
                        foreach (var title in new[]
                                 { "Controller", "DMC (part)", "Measurement", "n", "Mean", "StdDev", "Ref (xm)", "Tol (T)",
                                   "Cg/Cgk or %P/T", "Result", "Reason / notes" })
                            header.Cell().Background(Colors.Grey.Lighten2).Padding(4).Text(title).SemiBold();
                    });

                    IContainer Cell() => table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(4);

                    if (rows.Count == 0)
                    {
                        table.Cell().ColumnSpan(11).Padding(6).Text("No measurements.").Italic();
                    }
                    else
                    {
                        foreach (var row in rows)
                        {
                            Cell().Text(row.Controller);
                            Cell().Text(row.Dmc);
                            Cell().Text(row.Measurement);
                            Cell().Text(row.N.ToString(CultureInfo.InvariantCulture));
                            Cell().Text(Fmt(row.Mean));
                            Cell().Text(Fmt(row.StdDev));
                            Cell().Text(Fmt(row.Reference));
                            Cell().Text(Fmt(row.Tolerance));
                            Cell().Text(row.Metric);
                            // Non-evaluated rows must not read as PASS/FAIL — show n/a (task 2/4).
                            Cell().Text(t =>
                            {
                                if (!row.Evaluated)
                                    t.Span("n/a").FontColor(Colors.Grey.Darken1);
                                else
                                    t.Span(row.Passed ? "PASS" : "FAIL").SemiBold()
                                        .FontColor(row.Passed ? Colors.Green.Darken2 : Colors.Red.Darken2);
                            });
                            Cell().Text(row.Reason);
                        }
                    }
                });

                page.Footer().Column(col =>
                {
                    col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    col.Item().PaddingTop(3).Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                        t.Span("Generated by HarryDataServer V2 — ");
                        t.Span(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        t.Span("     Page ");
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            });
        });

    private static string ExpText(string e) => e switch
    {
        "reject" => "error expected",
        "accept" => "good expected",
        _ => string.IsNullOrEmpty(e) ? "—" : e,
    };

    private static string ActText(string a) => a switch
    {
        "rejected" => "rejected",
        "accepted" => "accepted",
        "no measurement" => "no measurement",
        "not evaluated" => "not evaluated",
        _ => string.IsNullOrEmpty(a) ? "—" : a,
    };

    /// <summary>
    /// LimitSample-specific report (task B): a compact head (BaseID, part DMC, verdict + reason,
    /// reference file, loops, criterion), a "prepared errors x of y detected" section, a "deviations"
    /// section (undetected prepared error / rejected good feature), then a compact
    /// Measurement | Expected | Actual | Result | Reason table. Summary sections always reflect the
    /// whole part (<paramref name="report"/>.Rows); <paramref name="tableRows"/> is the shown set
    /// (all rows for the complete report, only failures for the failures-only report). 2–4 pages.
    /// </summary>
    private static IDocument BuildLimitSampleDocument(
        MsaReportData report, IReadOnlyList<MsaReportRow> tableRows, string referenceFile,
        DateTime? referenceModified, string? legacyReferenceFile) =>
        Document.Create(container =>
        {
            var all = report.Rows;
            var rejectRows = all.Where(r => r.Evaluated && string.Equals(r.Expected, "reject", StringComparison.OrdinalIgnoreCase)).ToList();
            var detected = rejectRows.Count(r => r.Passed);
            var deviations = all.Where(r => r.Evaluated && !r.Passed).ToList();

            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Text($"LimitSample report — {report.Module}").FontSize(15).SemiBold();
                    col.Item().PaddingTop(2).Text(t =>
                    {
                        t.Span("Run: ").SemiBold();
                        t.Span(report.RunAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        t.Span($"     BaseID: {report.BaseId}");
                    });
                    col.Item().Text(t =>
                    {
                        t.Span("Part DMC: ").SemiBold(); t.Span(string.IsNullOrEmpty(report.Dmc) ? "—" : report.Dmc);
                        t.Span($"     Controller: {report.Controller}     Loops: {report.LoopCount}");
                    });
                    col.Item().Text(t =>
                    {
                        t.Span("Criterion: ").SemiBold();
                        t.Span(string.IsNullOrEmpty(report.Criterion) ? "—" : report.Criterion);
                    });
                    col.Item().Text(t =>
                    {
                        t.Span("Reference file: ").SemiBold();
                        if (string.IsNullOrEmpty(referenceFile))
                            t.Span("(none found)");
                        else
                        {
                            t.Span(referenceFile);
                            t.Span(referenceModified is { } m ? $"  (modified {m:yyyy-MM-dd HH:mm:ss})" : "  (NOT FOUND)");
                        }
                    });
                    if (!string.IsNullOrEmpty(legacyReferenceFile))
                        col.Item().Text(t => t.Span($"Legacy reference: {legacyReferenceFile} (used)").FontColor(Colors.Orange.Darken2));

                    col.Item().PaddingTop(3).Text(t =>
                    {
                        t.Span("Result: ").SemiBold();
                        var (label, color) = report.Verdict switch
                        {
                            MsaVerdict.Pass => ("PASS", Colors.Green.Darken2),
                            MsaVerdict.Fail => ("FAIL", Colors.Red.Darken2),
                            _ => ("INVALID", Colors.Orange.Darken2),
                        };
                        t.Span(label).Bold().FontColor(color);
                        if (!string.IsNullOrEmpty(report.VerdictReason))
                            t.Span($"  —  {report.VerdictReason}").FontColor(color);
                    });
                    foreach (var warning in report.ControllerWarnings)
                        col.Item().Text(t => t.Span("⚠ " + warning).FontColor(Colors.Orange.Darken2).SemiBold());
                    foreach (var note in report.Notes)
                        col.Item().Text(t => t.Span("• " + note).FontColor(Colors.Grey.Darken1));
                    col.Item().PaddingTop(3).LineHorizontal(1).LineColor(Colors.Grey.Medium);
                });

                page.Content().PaddingVertical(6).Column(col =>
                {
                    // Section 1: prepared errors detected.
                    col.Item().Text(t =>
                    {
                        t.Span($"Checked limit-sample errors: {detected} of {rejectRows.Count} detected").SemiBold();
                    });
                    if (rejectRows.Count == 0)
                        col.Item().PaddingLeft(10).Text("no expected errors in this reference (good reference)").Italic().FontColor(Colors.Grey.Darken1);
                    else
                        foreach (var r in rejectRows)
                            col.Item().PaddingLeft(10).Text(t =>
                            {
                                t.Span("• " + r.Measurement + ": ");
                                t.Span(r.Passed ? "detected" : "NOT detected")
                                    .SemiBold().FontColor(r.Passed ? Colors.Green.Darken2 : Colors.Red.Darken2);
                            });

                    // Section 2: deviations.
                    col.Item().PaddingTop(8).Text(t => t.Span("Deviations").SemiBold());
                    if (deviations.Count == 0)
                        col.Item().PaddingLeft(10).Text("none").Italic().FontColor(Colors.Green.Darken2);
                    else
                        foreach (var r in deviations)
                            col.Item().PaddingLeft(10).Text(t =>
                            {
                                var kind = string.Equals(r.Expected, "reject", StringComparison.OrdinalIgnoreCase)
                                    ? "expected error NOT detected (false good)"
                                    : "good feature rejected (false reject)";
                                t.Span("• " + r.Measurement + " — ").SemiBold();
                                t.Span(kind).FontColor(Colors.Red.Darken2);
                            });

                    // Section 3: compact per-measurement table.
                    col.Item().PaddingTop(10).Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(3.2f);  // Measurement
                            c.RelativeColumn(1.6f);  // Expected
                            c.RelativeColumn(1.6f);  // Actual
                            c.RelativeColumn(1.0f);  // Result
                            c.RelativeColumn(3.2f);  // Reason
                        });
                        table.Header(header =>
                        {
                            foreach (var title in new[] { "Feature", "Expected", "Actual", "Result", "Reason / note" })
                                header.Cell().Background(Colors.Grey.Lighten2).Padding(4).Text(title).SemiBold();
                        });
                        IContainer Cell() => table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(4);
                        if (tableRows.Count == 0)
                            table.Cell().ColumnSpan(5).Padding(6).Text("No measurements.").Italic();
                        else
                            foreach (var row in tableRows)
                            {
                                Cell().Text(row.Measurement);
                                Cell().Text(ExpText(row.Expected));
                                Cell().Text(ActText(row.Actual));
                                Cell().Text(t =>
                                {
                                    if (!row.Evaluated)
                                        t.Span("n/a").FontColor(Colors.Grey.Darken1);
                                    else
                                        t.Span(row.Passed ? "ok" : "NOK").SemiBold()
                                            .FontColor(row.Passed ? Colors.Green.Darken2 : Colors.Red.Darken2);
                                });
                                Cell().Text(row.Reason);
                            }
                    });
                });

                page.Footer().Column(col =>
                {
                    col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    col.Item().PaddingTop(3).Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                        t.Span("Generated by HarryDataServer V2 — ");
                        t.Span(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        t.Span("     Page ");
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            });
        });

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "x";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
