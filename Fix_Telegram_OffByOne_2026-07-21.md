# Fix: measurement `telegram_place` off-by-one (2026-07-21)

## Symptom
`msa_measurements` **and** `measurements_serial(_trimmer)` for M2X/M5X cameras stored the R_ **status
(0/1/2)** in `measurement_value` and left `result_status` **NULL**; the real analog value was lost.
Originally believed to be MSA-only and an upstream camera/telegram issue.

## Root cause — stale live config, not the app code, not the camera
The real "Results" telegram (confirmed from raw captures, identical in Normal and MSA mode):

| Token | Content |
|-------|---------|
| 67–70 | mode flags |
| **71** | `Total_Result` (single value — display only) |
| **72,73** | first measurement pair: **R_ status**, **V_ value** |
| 74,75 … | further (status, value) pairs |

Extraction is fully template-driven (`telegram_place` + `type`). The **repo** templates were all
correctly **72-based**, but the **live** `F:\002_Configs\Templates` M20/M21/M50 `Result_*.json` were
still **71-based (stale)** — one version behind. So every R_ definition read token 71
(`Total_Result`)/a V float and every V_ read an R status; the startup definition-sync persisted the
wrong `telegram_place`, scrambling value ↔ status.

M10/M11 live templates were already 72-based → M1X was correct. Both **M2X and M5X production AND
MSA** were affected identically.

DB evidence (before fix):
- `M11_ST030` (72-based): `R_GlueDot_1_Volume` → value 0.104, status 1 ✅
- `M50_ST110` (71-based): `R_Anode_Flatness_L` → value 1, status 0; rest value 1, status NULL ❌

## Fix
- Corrected the 10 live `Result_*.json` for M20/M21/M50 in `F:\002_Configs\Templates`: **+1 on every
  `telegram_place`** (now 72-based). Backups in `F:\002_Configs\Templates\_backup_offbyone_20260721\`.
  Takes effect on the next application start (definition-sync reloads and versions the definitions).
- `M50_ST040_KF1`: kept the live structure (incl. `Trimmer_Seated`) and shifted +1, verified against
  the Keyence ST040 "Datenausgabe" (two constant literals at 72/73, first pair Trimmer_Presence at
  74/75). The corrected ST040 template was also mirrored into the repo (it had been missing
  `Trimmer_Seated`).
- **No application code changed** — the extraction/pairing code was already correct.
- Fixed the misleading CLAUDE.md §9 example (first pair 71/72 → 72/73) that had led to the stale
  numbering, with a note.

## `Total_Result` (the leading `+0`, token 71)
Camera overall result, **display only** (`ParsedTelegram.OverallResult`); not stored. The
authoritative OK/NG comes from the PLC at part-exit. Not added to `msa_results`.

## Regression test
New dependency-free runner `HarryDataServer.Tests` (no xUnit/NuGet, so it builds offline on the
line). Run: `dotnet run --project HarryDataServer.Tests -c Debug`. Cases (against real captured
telegram lines):
- **A** MSA3 M50, correct 72-based → `0.008`/status 1, `-0.620`/status 0, `99.000`/status 2.
- **B** same telegram, OLD 71-based → scrambled (value 1.0, real value lost) — pins the bug.
- **C** Normal M11 production, 72-based → `0.043`/status 1 — proves the shared path is correct.

Result: **ALL TESTS PASSED** (exit 0). Debug + full Release solution build: 0 errors.

## Follow-up (operator)
Restart HarryDataServer so the corrected live templates are re-synced. Historical rows written
before the restart remain scrambled (they can be re-derived from the raw captures / re-measured if
needed); new rows will be correct.
