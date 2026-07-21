using System.Globalization;
using System.IO;
using HarryDataServer.Infrastructure;
using HarryDataServer.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

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
        // Prefer the folder the MSA engine already resolved (report path with network fallback, task D).
        var dir = report.OutputDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            var msa = _config.Config.Msa;
            dir = MsaResultLayout.EnsureWritableReportDir(
                      msa.ReportPath, msa.ReportFallbackPath, msa.ResultPath, report.Module, report.RunAt, _log)
                  ?? MsaResultLayout.ReportModuleDate(MsaResultLayout.DefaultReportFallback, report.Module, report.RunAt);
        }
        var baseName = $"{Sanitize(report.Module)}_{Sanitize(report.TestType)}_{Sanitize(report.BaseId)}_{FileNaming.Stamp(report.RunAt)}";
        return new MsaReportPaths(
            Path.Combine(dir, baseName + "_AllResults.pdf"),
            Path.Combine(dir, baseName + "_FailuresOnly.pdf"));
    }

    public MsaReportPaths Generate(MsaReportData report)
    {
        var paths = ResolvePaths(report);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.AllResults)!);

        BuildDocument(report, report.Rows).GeneratePdf(paths.AllResults);

        var failures = report.Rows.Where(r => !r.Passed).ToList();
        BuildDocument(report, failures).GeneratePdf(paths.FailuresOnly);

        _log.Information("MSA PDF reports written for {Module}/{Type} ({Failures} failure(s)): {All} | {Fail}",
            report.Module, report.TestType, failures.Count, paths.AllResults, paths.FailuresOnly);
        return paths;
    }

    private static string Fmt(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";

    private static IDocument BuildDocument(MsaReportData report, IReadOnlyList<MsaReportRow> rows) =>
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
                        t.Span(string.IsNullOrEmpty(report.ReferenceFile) ? "(none configured)" : report.ReferenceFile);
                        t.Span(report.ReferenceFileModified is { } m
                            ? $"  (modified {m:yyyy-MM-dd HH:mm:ss})"
                            : "  (NOT FOUND)");
                    });
                    col.Item().PaddingTop(3).Text(t =>
                    {
                        t.Span("Overall result: ").SemiBold();
                        var span = t.Span(report.OverallPass ? "PASS" : "FAIL").Bold();
                        if (report.OverallPass) span.FontColor(Colors.Green.Darken2);
                        else span.FontColor(Colors.Red.Darken2);
                    });
                    col.Item().PaddingTop(3).LineHorizontal(1).LineColor(Colors.Grey.Medium);
                });

                page.Content().PaddingVertical(6).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(2.2f); // Controller
                        c.RelativeColumn(3);    // Measurement
                        c.RelativeColumn(0.7f); // n
                        c.RelativeColumn(1.3f); // Mean
                        c.RelativeColumn(1.3f); // StdDev
                        c.RelativeColumn(1.2f); // Reference
                        c.RelativeColumn(1.2f); // Tolerance
                        c.RelativeColumn(2.4f); // Cg/Cgk or %P/T
                        c.RelativeColumn(0.9f); // Result
                        c.RelativeColumn(3.5f); // Reason / notes
                    });

                    table.Header(header =>
                    {
                        foreach (var title in new[]
                                 { "Controller", "Measurement", "n", "Mean", "StdDev", "Ref (xm)", "Tol (T)",
                                   "Cg/Cgk or %P/T", "Result", "Reason / notes" })
                            header.Cell().Background(Colors.Grey.Lighten2).Padding(4).Text(title).SemiBold();
                    });

                    IContainer Cell() => table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(4);

                    if (rows.Count == 0)
                    {
                        table.Cell().ColumnSpan(10).Padding(6).Text("No measurements.").Italic();
                    }
                    else
                    {
                        foreach (var row in rows)
                        {
                            Cell().Text(row.Controller);
                            Cell().Text(row.Measurement);
                            Cell().Text(row.N.ToString(CultureInfo.InvariantCulture));
                            Cell().Text(Fmt(row.Mean));
                            Cell().Text(Fmt(row.StdDev));
                            Cell().Text(Fmt(row.Reference));
                            Cell().Text(Fmt(row.Tolerance));
                            Cell().Text(row.Metric);
                            Cell().Text(t =>
                                t.Span(row.Passed ? "PASS" : "FAIL").SemiBold()
                                 .FontColor(row.Passed ? Colors.Green.Darken2 : Colors.Red.Darken2));
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

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "x";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
