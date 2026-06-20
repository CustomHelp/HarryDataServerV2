# HarryDataServer V2 — Companion Tools Build Guide

*For a fresh Claude Code instance.* This document is a self-contained handoff: current
status, the conventions/architecture to reuse, the database schema you will query, a
per-tool spec, and the live-test checklist. Read `CLAUDE.md` (master spec) and `STATUS.md`
(detailed phase status) alongside this file.

---

## 1. Status snapshot (2026-06-20)

The **main server** (`HarryDataServer`) is built through Phase 11b and runs:
- DB bootstrap + auto-migration (ADD COLUMN on startup), daily partitions, retention.
- 14 camera TCP clients, measurement/settings/diagnostic pipelines.
- 7-channel SPS server incl. KeepAlive health word + **channel-2 part-exit orchestration**
  (save `dmcserial` → parallel CSV/Collage/Images → V1 ACK).
- MSA engine (Cg/Cgk/%Tolerance/LimitSample) + read path for the UI.
- WPF dashboard (dark theme): Overview, Cameras, SPS, MSA, Database, CSV, Collage, Log.

**Built (2026-06-20): all 5 companion tools.** Implemented as **separate WPF projects in
the same solution** (`HarryDataServer.sln`), per CLAUDE.md §16, sharing a new
**`HarryShared`** class library (dark theme, converters, logo, read-only `HarryConfig` +
`QueryService` + `MsaReferenceFile` + `CsvExport`). Projects: `HarryShared`,
`HarryAnalysis`, `HarryGraph`, `HarryCounter`, `HarryLimitSample`, `HarryCollageCreator`.
The full solution builds **0 warnings / 0 errors**. What remains is the **on-site
live-test** (§5) against a populated database / real images — the office build had no
data, so the queries and renderers are verified by compilation + design, not yet by live
results.

Build/run: `dotnet build HarryDataServer.sln -c Release` (0 warnings / 0 errors,
`net8.0-windows`). Repo: `https://github.com/CustomHelp/HarryDataServerV2`, branch `main`.

### Implementation notes (per tool)
- **Read-only access:** all four DB tools go through `HarryShared.Data.QueryService` using
  the **GetData** account. `HarryConfig` reads Server/Database from Harry.ini `[MySQL]`;
  the read-only user defaults to `GetData`/`1234Get` and is overridable via
  `[MySQL] GetUser` / `GetPassword` (added keys) so it survives the post-deploy password change.
- **Limits (HarryAnalysis):** latest Min/Max folded per `(camera_id, parameter_set)` from
  the `settings` history (matches the MSA engine's tolerance assumption — verify on-site).
- **HarryGraph:** picks the serial vs trimmer table by module (M20/M21 → trimmer).
- **HarryLimitSample:** writes `MSA_<module>.json` matching the server's `MsaReferenceFile`
  schema; merges into an existing file so the `references` (xm) block is preserved.
- **HarryCollageCreator:** the live preview reuses the **exact** `CollageComposer` geometry
  (Pos=centre, size = crop × Scale × Zoom, crop→scale→mirror→place). Since that geometry is
  itself **pending on-site verification** against a V1 collage, re-check the creator's
  output alongside the server's when that check happens, and keep the two in sync.

### UX review pass 1 (2026-06-20)
- **All tools:** `DarkTheme.xaml` now templates the `ComboBox` (dark `#22262F` popup) and adds a
  readable `ComboBoxItem` style — fixes the low-contrast dropdowns everywhere.
- **HarryAnalysis:** 3-field search (DMC / SZID / VirtualSerial) with matched-field display;
  in-memory scan-history list (last 20, click-to-load, clear-all, right-click remove) over a
  detail panel; sortable measurement grid; export via `SaveFileDialog` defaulting to
  `[CSV] CSV_BasePath` as `Analysis_<DMC>_<date>.csv`.
- **HarryCounter:** multi-level `TreeView` (`ErrorTreeNode`) with grouping order chosen by three
  ComboBoxes and an OK/NG leaf breakdown — *RazorErrorCount source was not present on the
  machine (not in git history or on disk), so the hierarchy was built from the written spec.*
  New shared query `GetErrorTreeRowsAsync`.
- **HarryGraph:** 1–6 panels (WrapPanel, `+`/`–`), per-panel measurement selector, full-screen
  maximize window, dark tracker tooltip, X-only zoom + Lock-Y + Reset Zoom, and a point-by-point
  time-varying Min/Max envelope via `GetLimitHistoryAsync` (`LimitHistory.MinAt/MaxAt`).
- **HarryLimitSample:** shows the full `MSA_<module>.json` save path; default Expected derived
  from each measurement's `result_status`.
- New shared helpers: `HarryConfig.CsvBasePath`, `QueryService.GetErrorTreeRowsAsync` +
  `GetLimitHistoryAsync`, models `ErrorAggRow` / `LimitHistory`.

### UX review pass 2 (2026-06-20)
- **Theme:** added dark `ContextMenu`/`MenuItem` and `DatePicker`/`DatePickerTextBox`/`Calendar`
  styles (the right-click menu and date fields were grey-on-grey).
- **HarryAnalysis:** **Export All** — every scanned part into one CSV (part-identity columns +
  measurements).
- **HarryCounter:** bar chart restored beside the tree (top-level NG counts).
- **HarryGraph:** multi-measurement per panel (checkable popup + filter, per-series colour and
  dashed envelope); detail window is a normal **resizable** window (no forced maximize); panels
  re-layout on app maximize/restore.
- **HarryCollageCreator:** `Open`/`Save` guard `InitialDirectory` (only set when the directory
  exists) — fixes the `ArgumentException` on Open.
- **HarryLimitSample:** Save dedupes `display_name` keys (ShouldFail wins, conflicts reported)
  so it no longer crashes. **Reference model:** one `MSA_<module>.json` per module in
  `[MSA] ReferencePath`, keyed by `display_name`; remove an entry via *Load existing → set row to
  Ignore → Save* (the `references`/xm block is preserved).

---

## 2. Conventions to reuse (match the main app)

- **Target:** `net8.0-windows`, `<UseWPF>true</UseWPF>`, `Nullable` + `ImplicitUsings` enabled,
  `LangVersion=latest`. All code/comments/UI text in **English**.
- **Per-app icon:** `F:\001_CHP\Programme\HarrySuite_Icons\<AppName>.ico` via
  `<ApplicationIcon>`. Available: HarryAnalysis, HarryGraph, HarryCounter,
  HarryCollageCreator, HarryLimitSample, HarryDataServer, HarrySimulator.
- **CustomHelp logo:** `F:\001_CHP\Programme\CUSTOMHELP_Logo-links_lila.png` — top-left of
  every MainWindow, height 32px (embed as a WPF `<Resource>` with `Link="Assets\..."`).
- **Dark theme:** copy/share `HarryDataServer\Themes\DarkTheme.xaml` (`#1A1D23` bg, purple
  `#6B21A8` accent; implicit styles for TextBlock/Button/ToggleButton/TabControl/DataGrid/
  ListBox/StatusBar; `PassToBrush`/`PassToText`/`BoolToVisible` converters). Merge it in
  each tool's App.xaml.
- **MVVM:** `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `RelayCommand`,
  `AsyncRelayCommand`). For live views use a single 1 s `DispatcherTimer` on the UI thread
  and pull from services (avoids cross-thread binding); marshal background events with
  `Dispatcher.Invoke`. Frozen `SolidColorBrush` for LED/status colours (see `ViewModels\Led.cs`).
- **DI:** `Microsoft.Extensions.DependencyInjection`; singletons composed in `App.xaml.cs`
  `BuildServiceProvider()`, window resolved from the container.
- **Config:** reuse the `IniConfigManager`/`IConfigService` pattern. Harry.ini resolution
  order: `HARRY_CONFIG_DIR` env → `F:\002_Configs` → next to the exe → legacy
  `D:\HarryDataServer`. Relative paths resolve against the Harry.ini directory.
- **DB access:** `MySqlConnector`. **One `MySqlConnection` per operation** (pooled, never
  shared across threads); `ConfigureAwait(false)` on every async I/O.
- **Logging:** Serilog (`SerilogService`), daily rolling files, 30-day retention.

### Database users (CLAUDE.md §8)
- **Read-only tools → `GetData` / `1234Get`** (SELECT only, network access). Use this for
  HarryAnalysis, HarryGraph, HarryCounter, HarryLimitSample (loading).
- `SettData` / `1234Set` (full DDL+DML, localhost) is the server's account — do **not** use
  it for companion tools.
- Connection: server `localhost`, database `camera_data`. (Customer changes passwords after
  deployment — read them from Harry.ini `[MySQL]`, don't hardcode.)

> Do **not** regenerate or overwrite the JSON camera templates in
> `Resources\Templates\*.json` — they are customer-owned (the app only reads them).

---

## 3. Database schema (what you will query)

Source of truth: `HarryDataServer\Infrastructure\DatabaseSchema.cs`. Key tables:

- **`dmcserial`** — one row per finished part: `serial_number` (SZID), `serial_trimmer`,
  `dmc`, `m1x_module/nest`, `m3x_module/nest`, `m50_nest`, `order_name`, `m1x_humidity`,
  `result_status` (1=OK, 0=NG, -1=deleted), `created_at`.
- **`measurements_serial`** / **`measurements_serial_trimmer`** (partitioned by day):
  `serial_number`/`serial_trimmer`, `definition_id`, `measurement_value`,
  `measurement_string`, `result_status`, `run_type` (0=Normal,1=MSA1,2=MSA3,3=LimitSample,
  4=GoldenSample), `measured_at`. (R_/V_ pairs are combined into one row keyed by the R_
  definition: result in `result_status`, value in `measurement_value`.)
- **`measurement_definitions`** — `id`, `camera_id`, `telegram_place`, `variable_name`
  (R_/V_ prefixed), `display_name`, `var_type`, `parameter_set`, `module_ref`,
  `feature_group` (error category for HarryCounter), `effective_from/end` (NULL = active).
- **`setting_definitions`** + **`settings`** — limit history per (camera, definition,
  parameter_set): `limit_value`, `limit_type` (Min/Max), `recorded_at`. USL−LSL =
  latest Max − latest Min for a (camera_id, parameter_set).
- **`cameras`** — `id`, `camera_name`, `module`, `ip_address`, `port`.
- **`msa_results`** — per-measurement MSA outcome: `controller_name`, `dmc`, `base_id`,
  `msa_type` (MSA1/MSA3/LimitSample), `definition_id`, `display_name`, `cg_value`,
  `cgk_value`, `pct_tolerance`, `expected_value`, `actual_value`, `passed`, `evaluated_at`.
  A "run" = all rows sharing a `base_id`. Module = `controller_name LIKE 'M50%'` etc.
- **`msa_measurements`** — raw MSA samples (dmc, base_id, loop_number, controller_name,
  definition_id, value/string/result, msa_type, measured_at).

Joins you'll reuse: measurements → `measurement_definitions` (definition_id) →
`cameras` (camera_id) for display names / module / feature_group.

---

## 4. Companion tools — per-tool spec (CLAUDE.md §16)

Each is a new WPF project (`dotnet new wpf`) added to the solution; reuse §2 conventions,
the dark theme, the logo, and its own `.ico`. **MSA is already done** as a tab in the main
app (`ucMsaControl`) — no separate HarryMSA needed. `HarrySimulator` is the customer's own
test program (don't rebuild).

### 4.1 HarryAnalysis — scanner / part inspector  (icon: HarryAnalysis.ico, user: GetData)
- Operator scans a DMC barcode (or types serial). Fetch **all** data for that part:
  - `dmcserial` row by `dmc` or `serial_number` (general info: order, humidity, result, nests).
  - All measurements: join `measurements_serial`(+`_trimmer`) on the serial(s) →
    `measurement_definitions` for `display_name`, `feature_group`, `var_type`.
  - Limits: latest Min/Max from `settings`+`setting_definitions` per (camera, parameter_set).
- Display a grid: DisplayName | Value | Min | Max | Result (colour OK/NG). Plus header card
  with the general info. Export the view to CSV.
- UI: a scan TextBox (focus-on-load, Enter to query), result DataGrid, export button.

### 4.2 HarryGraph — measurement time-series  (icon: HarryGraph.ico, user: GetData)
- Pick one or more measurements from the `measurement_definitions` list (active ones).
- Plot value over `measured_at` (OxyPlot.Wpf `LineSeries`, DateTimeAxis). Query
  `measurements_serial` by `definition_id` within a time range.
- Modes: **Live** (auto-refresh via 1 s timer, rolling window) or **fixed range**
  (from/to date pickers). Zoom/pan (OxyPlot built-in), print, and save/load the graph
  config (selected definitions + range) as JSON.
- NuGet: `OxyPlot.Wpf`.

### 4.3 HarryCounter — NG error counter  (icon: HarryCounter.ico, user: GetData)
- Port of the old RazorErrorCount. Count NG parts/measurements by time period.
- Group by **error category** = `measurement_definitions.feature_group`; group by **nest**
  (`dmcserial.m50_nest` / m1x_nest / m3x_nest). Live view + historical (date range).
- Queries: NG parts = `dmcserial WHERE result_status=0`; failing measurements =
  `measurements_serial WHERE result_status=0` joined to definitions for `feature_group`.
- UI: filter bar (range, grouping), a results DataGrid / bar chart, live toggle.

### 4.4 HarryCollageCreator — Collage.ini layout editor  (icon: HarryCollageCreator.ico)
- Visual editor that **writes** `Collage.ini` (the format the server reads — see
  `CollageIniReader`/`CollageLayout`/`CollageImageSpec`). Sections: `[CollageSettings]`
  (CanvasWidth/Height/BackgroundColor) and `[ImageN]` (TemplateName, Pos_X/Y, Scale, Zoom,
  Crop_X/Y/Width/Height, Mirror_X/Y, KeyName).
- Load sample BMPs, place/zoom/crop/mirror on a canvas (System.Drawing or WPF imaging),
  show the live composite, and save the resulting Collage.ini. **Matching at runtime is by
  serial (with "_" after char 12) + all KeyName keywords present in the filename** — keep
  KeyName authoring consistent with that.
- This is the **authoring** counterpart to the server's `CollageComposer` (whose rendering
  semantics are still pending on-site verification — keep them in sync).

### 4.5 HarryLimitSample — LimitSample reference editor  (icon: HarryLimitSample.ico)
- Scan a part DMC → load its measurements (read, GetData) → mark each measurement as
  "should pass" / "should fail" / "ignore" → **save a LimitSample JSON reference file**
  (`MSA_<module>.json`, the `LimitSampleExpected` map keyed by `display_name`; see
  `MsaReference`/`MsaReferenceLoader` and `[MSA] ReferencePath`). Add/delete entries.
- This produces the reference the MSA engine uses to evaluate LimitSample runs
  (`expected_value` = reject/accept).

---

## 5. Live-test checklist (must be verified on-site, with hardware/PLC)

The office tests used the customer's HarrySimulator (no real cameras/PLC). The following
need verification on the line, in order:

1. **DB + pipelines:** cameras connect, measurements/settings land in MySQL, KeepAlive
   health word reflects faults (stop MySQL → `…;ERROR;…`; restart → back to `…;OK` within
   ~5 s via the heartbeat).
2. **Part-exit channel 2 under load (450 ms budget):** send real Part Exit telegrams; watch
   the **CSV / Collage tab "Part-exit timing"** line (CSV | Collage | Images | Total) and the
   Log for any `> 450ms` WARNING. Confirm the **ACK** format
   `serial.PadRight(32,'0') + ";" + true|false + "\r\n"` is what the PLC expects, and that
   `true`/`false` reflects success.
3. **Collage rendering (no V1 source to port from):** with sample images + a real Collage.ini,
   enable `Collage_Generate=true`, send an OK Part Exit, and compare the output JPG to a V1
   collage. Verify the assumptions: Pos = image centre, size = crop × Scale × Zoom,
   crop→scale→mirror→place; file matching = serial (`_` after char 12) + all KeyName keywords.
   Adjust `CollageComposer` if needed. Check thumbnails appear on the Collage tab.
4. **Image handling:** with `DeletePictures=false` (default, safe) confirm images are copied to
   `BackupFolder\YYYY\MM\DD\HH\`, size-verified, then deleted. Only switch `DeletePictures=true`
   once trusted.
5. **MSA:** trigger a real MSA1/MSA3/LimitSample run (channels 3–7), confirm `msa_results`
   populates and the MSA tab shows Run X of Y, badge, and LimitSample Expected/Actual.

### Ships safe by default (`Harry.ini`)
`Collage_Generate=false`, `DeleteAfterCollage=false`, `DeletePictures=false` — enable each
only after the corresponding on-site check above passes.

---

## 6. Suggested order

HarryAnalysis (simplest, read-only, validates the DB read patterns) → HarryGraph (adds
OxyPlot) → HarryCounter → HarryLimitSample → HarryCollageCreator (most visual). Build each
as its own project, get it to 0/0 + a launch smoke test, then commit.
