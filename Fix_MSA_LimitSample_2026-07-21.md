# LimitSample-MSA: 4 fixes (2026-07-21, BaseID 50260721170000)

## 1 — Reference file "not loaded" (path resolution + logging)
The file *was* loaded (only `CameraNR` was in it at run time; the full all-false file was written by
the teach run afterwards). Two real defects: the success log showed `references.Count` (=0) and **no
path**, and the **on-demand UI report** (`MsaReportData.FromRun`) never filled the head → misleading
`(none configured)`.
- `MsaReferenceLoader.Load` now always logs the **full resolved path** + both counts
  (`LOADED from <path> — N xm, M limit-sample (K expected reject)` / `NOT FOUND at <path>`).
- `PdfReportService` fills the reference-file head from config when the report didn't carry it.
- `FromRun` uses the **BaseID timestamp** for `RunAt`, so the UI "Open" button finds and opens the
  original auto-generated PDF (full head) instead of regenerating a stripped one.

## 2 — No more vacuous PASS (tri-state verdict)
New `MsaVerdict {Invalid, Pass, Fail}`. Only `Evaluated` measurements count. A run is **Invalid**
(pushed as `Error;<reason>`, not OK) when there are no measurements, **0 evaluated**, or — LimitSample
— **no expected error (prepared reject) was checked**. PASS requires ≥1 evaluated, all evaluated
passed, and (LimitSample) ≥1 expected reject verified. Per-row `evaluated` is stored in `msa_results`
so the UI recomputes the same verdict.

## 3 — Teach-in guard (HarryLimitSample)
Teach already maps status 1→ShouldPass, 0→ShouldFail, else Ignore. Now **Save is refused** when the
module's controllers produced no judgement (only status 2/99) in the scanned part
("Kamera hat nicht bewertet – Einlernen nicht möglich"); a reference with **0 ShouldFail** is warned
(would make the run Invalid); not-judged cameras are named in the save status.

## 4 — Camera-did-not-judge warning
A controller that produced no real OK/NOK in the run (only status 2/−1) is shown per controller in
the report head + log ("camera did not evaluate (only status 2/−1) — check program/mode"); its
LimitSample features are neutralised (neither pass nor fail) so they cannot create a false PASS.

## Diagnosis note
Root of the observed all-PASS: at run time the reference held only `CameraNR`, so every other feature
was "no reference entry → not evaluated → passed" and the overall was a vacuous PASS. The empty
`settings` table (tolerance 0) remains the operator follow-up for MSA1/3.

## Files
`Models/{MsaModels,MsaReport,MsaRunDto}.cs`, `Services/{MsaEvaluationText,MsaService,PdfReportService}.cs`,
`Configuration/MsaReference.cs`, `Infrastructure/DatabaseSchema.cs`, `HarryLimitSample/MainViewModel.cs`,
`HarryDataServer.Tests/Program.cs`, `CLAUDE.md`. No change to `F:\002_Configs`.

## Tests / build
`HarryDataServer.Tests` adds Case J (verdict logic: vacuous→INVALID, reject-detected→PASS,
reject-missed→FAIL, MSA3 degenerate→INVALID). **ALL TESTS PASSED.** Debug + full Release build:
**0 errors** (1 pre-existing unrelated warning in HarryAnalysis).

## Operator follow-up
App restart for the new `evaluated` column/code; teach a real border sample (camera must report the
prepared errors as NOK/status 0) so the reference has ShouldFail entries; populate `settings`
(Settings anfordern) for MSA1/3 tolerance.
