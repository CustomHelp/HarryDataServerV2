# MSA evaluation & reporting overhaul (2026-07-21)

## Trigger
First MSA3 run after the telegram off-by-one fix (BaseID `50260721150209`, M50_ST110) showed every
measurement FAIL with Expected empty, Actual/%P/T = 0, and each measurement doubled.

## Root causes (diagnosis)
1. **All %P/T = 0 → all FAIL:** the `settings` table is **empty** (0 rows). Tolerance `T = Max − Min`
   comes from `settings`; with no limits `T = 0` and `MsaCalculator.Msa3` returns `(0, false)` by
   design. (`msa_measurements` values themselves were correct after the template fix.)
2. **Expected empty:** MSA3 does not use the reference file at all (only `T` + values); the report
   simply never filled Expected for MSA3.
3. **Doubled rows:** the run legitimately spans **two cameras** (`M50_ST110_KF1` + `KF3`, both in
   MSA3), which share `display_name`s. `GatherAsync` scopes by module (`controller_name LIKE 'M50%'`)
   and the report grouped by `definition_id` but showed **no controller** → each name appeared twice.
   Not two parts, not definition versioning.

## Changes
**B — meaningful report (never a silent 0/FAIL)**
- `MsaService.Evaluate` now returns n, mean, σ, reference (xm), tolerance (T), criterion, **reason**
  and **controller** per measurement. New pure `MsaEvaluationText` builds the criterion + plain-text
  FAIL reason (e.g. "no tolerance available (no Min/Max limits stored — request a Settings telegram)",
  "%P/T 34.2 % > 20 %", "only n=3 value(s) (need ≥ 2)", "Cgk 0.9 < 1.33"); every FAIL is also logged.
- PDF (now landscape): head with msa_type, controller(s), #parts (DMCs), #loops, time range,
  applied criterion and the reference file (full path + modified date / NOT FOUND); rows with
  Controller, n, Mean, StdDev, Ref, Tol, Cg/Cgk or %P/T, Result, Reason.
- `msa_results` gains `n_values, mean_value, std_dev, reference_value, tolerance, criterion, reason`
  (schema auto-added on startup); `controller_name`/`dmc` now stored per row.

**C — raw-data export (Minitab)**
- Long format `Controller;BaseID;Loop;DMC;Measurement;Value;Status;Timestamp` (all cameras/parts/
  loops/measurements), written next to the PDFs. Per CLAUDE.md §15 **no Excel library**: it is a
  `;`-separated UTF-8-BOM CSV that opens in Excel and imports 1:1 into Minitab.

**D — configurable paths**
- New `[MSA] ReportPath` → PDFs **and** raw export to `<ReportPath>\<Module>\<yyyy-MM-dd>\`.
- New `[MSA] ReportFallbackPath` (default `D:\HarryDataServer\MSA_Reports`): if the primary
  (network) path is unreachable at write time → fallback + WARNING, never a crash/data loss.
- Absolute local, mapped-drive and UNC paths supported for `ReportPath` and `ReferencePath`.
- Measurement summary CSV + images stay under `[MSA] ResultPath\YYYY\MM\DD\<BaseID>`.

**E — methodology documented** in CLAUDE.md §7 (MSA3 = one study per measurement over all parts,
loops = repetitions; %P/T = repeatability EV vs. tolerance, EV ≈ GRR for the vision system).

## Files
`Models/{AppConfig,MsaModels,MsaRunDto,MsaReport}.cs`, `Configuration/{IniConfigManager,MsaReference}.cs`,
`Infrastructure/{MsaResultLayout,DatabaseSchema}.cs`, `Services/{MsaService,PdfReportService}.cs`,
new `Services/MsaEvaluationText.cs`, `HarryDataServer/Harry.ini`, `HarryDataServer.Tests/Program.cs`,
`CLAUDE.md`. No application code deleted; no change to `F:\002_Configs`.

## Tests / build
`HarryDataServer.Tests` (dependency-free): telegram cases A–C + MSA cases D–H (tolerance=0 reason,
%P/T>20 reason, %P/T pass, MSA1 n<2 reason, Msa3 calc). **ALL TESTS PASSED.** Debug build and full
Release solution build: **0 errors** (1 pre-existing unrelated warning in HarryAnalysis).

## Operator follow-up
- To make the real 0/FAIL disappear, **limits must be captured** ("Settings anfordern" for the M50
  cameras) so `settings` is populated → tolerance > 0.
- The **live** `F:\002_Configs\Harry.ini` has no `ReportPath` yet → reports currently go to the local
  fallback `D:\HarryDataServer\MSA_Reports`. Set `[MSA] ReportPath` (e.g. `X:\MSA_Reports`) to route
  them to the network share.
