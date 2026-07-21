# MSA/LimitSample rebuild: per-part references + new report layout + run images (2026-07-21)

## A) LimitSample references: one file per part (DMC)
- New shared model `HarryShared/Data/LimitSampleReference.cs`. Path:
  `<ReferencePath>\<Module>\LimitSamples\<sanitized-DMC>.json` (`[MSA] ReferencePath`, resolved from
  Harry.ini by BOTH the server and the editor). Original DMC kept in the JSON; only judged features
  (status 0/1) are stored. Schema: `dmc, module, taught_at, source_base_id, controllers, expected`
  (`expected`: name → "ShouldFail"/"ShouldPass").
- Server `MsaService.EvaluateLimitSample`: each run DMC is checked against ITS file — every ShouldFail
  must be rejected for that part. Run DMC without a file → that part INVALID; taught DMC missing from
  the run → a note. Overall verdict via pure `MsaEvaluationText.LimitSampleOverall` (no vacuous PASS:
  no refs / part-without-ref / nothing evaluated / no prepared error → INVALID). Legacy module-wide
  `MSA_<Module>.json` `limit_sample_expected` remains a fallback with a WARNING ("old format").
- MSA1 xm stays module-wide in `MSA_<Module>.json`.
- Editor (HarryLimitSample): teach writes one file per scanned DMC; new "Taught parts" list
  (DMC / taught-at / #prepared errors) with Open/Delete; the fully resolved save path is shown/logged.
  Never-judged-source guard + zero-ShouldFail warning retained.

## B) Report layout (MSA1/MSA3/LimitSample)
`<ReportPath>\<yyyy-MM-dd>\<Module>\<BaseID>\` with `PDF\` (2 reports), `RAW\` (Minitab CSV), `IMG\`
(run images). Date = run day. Network fallback (`[MSA] ReportFallbackPath`) mirrors the layout. Old
flat files untouched (no migration). Measurement summary CSV still under `[MSA] ResultPath`.

## C) Run images
`MsaService.CopyRunImages` COPIES (never moves) the run's images from the GoldenSample NAS input
(`[NAS] HighResGoldenSamplePath`) into `IMG\`, matched by the 14-char BaseID in the filename. Missing
images are not a run error — only a log line "n found / m copied". (Analysis: the old `MoveRunImages`
moved GoldenSample-only images into the old ResultPath IMG tree, so it appeared inactive.)

## D) Tests / build
`HarryDataServer.Tests`: Case K (per-part verdict incl. unknown part / empty folder → INVALID),
Case L (per-part reference save/load/delete + DMC sanitize), Case M (report run-root layout).
**ALL TESTS PASSED.** Debug + full Release build: **0 errors** (1 pre-existing unrelated warning in
HarryAnalysis).

## Files
New `HarryShared/Data/LimitSampleReference.cs`; changed `Infrastructure/MsaResultLayout.cs`,
`Services/{MsaService,MsaEvaluationText,PdfReportService}.cs`, `Models/{MsaReport,MsaRunDto}.cs`,
`HarryLimitSample/{MainViewModel.cs,LimitSampleRow.cs,MainWindow.xaml}`, `HarryDataServer.Tests/Program.cs`,
`CLAUDE.md`. No change to `F:\002_Configs`.

## Open points
- Editor "Load existing" still reads the legacy `MSA_<Module>.json`; per-part editing is via the new
  "Taught parts" list. `source_base_id` is empty for a manually scanned teach part (no BaseID).
- Image source is GoldenSample only (per decision); if MSA1/3 images live elsewhere the log shows
  "0 copied".
- App restart needed for the new evaluation/layout to take effect on the line.
