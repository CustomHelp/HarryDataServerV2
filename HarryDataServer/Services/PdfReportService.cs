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
        // Per-run collection folder: <ResultPath>\YYYY\MM\DD\<BaseID>\PDF (date from the BaseID).
        var msa = _config.Config.Msa;
        var dir = MsaResultLayout.PdfDir(msa.ResultPath, msa.ReferencePath, report.BaseId);
        var baseName = $"{Sanitize(report.Module)}_{Sanitize(report.TestType)}_{FileNaming.Stamp(report.RunAt)}";
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

    private static IDocument BuildDocument(MsaReportData report, IReadOnlyList<MsaReportRow> rows) =>
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Text($"MSA Report — {report.Module}  {report.TestType}")
                        .FontSize(16).SemiBold();
                    col.Item().Text(t =>
                    {
                        t.Span("Run: ").SemiBold();
                        t.Span(report.RunAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        t.Span($"     Controller: {report.Controller}     BaseID: {report.BaseId}");
                    });
                    col.Item().PaddingTop(2).Text(t =>
                    {
                        t.Span("Overall result: ").SemiBold();
                        var span = t.Span(report.OverallPass ? "PASS" : "FAIL").Bold();
                        if (report.OverallPass) span.FontColor(Colors.Green.Darken2);
                        else span.FontColor(Colors.Red.Darken2);
                    });
                    col.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Grey.Medium);
                });

                page.Content().PaddingVertical(8).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(3); // Measurement
                        c.RelativeColumn(2); // Expected
                        c.RelativeColumn(2); // Actual
                        c.RelativeColumn(3); // Cg/Cgk or %P/T
                        c.RelativeColumn(1); // Pass/Fail
                    });

                    table.Header(header =>
                    {
                        foreach (var title in new[] { "Measurement Name", "Expected", "Actual Value", "Cg/Cgk or %P/T", "Result" })
                            header.Cell().Background(Colors.Grey.Lighten2).Padding(5).Text(title).SemiBold();
                    });

                    IContainer Cell() => table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(5);

                    if (rows.Count == 0)
                    {
                        table.Cell().ColumnSpan(5).Padding(6).Text("No measurements.").Italic();
                    }
                    else
                    {
                        foreach (var row in rows)
                        {
                            Cell().Text(row.Measurement);
                            Cell().Text(row.Expected);
                            Cell().Text(row.Actual);
                            Cell().Text(row.Metric);
                            Cell().Text(t =>
                                t.Span(row.Passed ? "PASS" : "FAIL").SemiBold()
                                 .FontColor(row.Passed ? Colors.Green.Darken2 : Colors.Red.Darken2));
                        }
                    }
                });

                page.Footer().Column(col =>
                {
                    col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    col.Item().PaddingTop(3).Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken1));
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
