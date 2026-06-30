# HarryDataServer V2 — Implementation Status

*Last updated: 2026-06-22*

> **Companion tools:** see **`COMPANION_TOOLS.md`** for the build guide (status, conventions,
> DB schema, per-tool spec, and the on-site live-test checklist).

> ## ⚠️ Pending on-site (live) verification
> - **Collage + part-exit image handling** are implemented from the **written spec**, not a
>   literal V1 port — **no V1 source (`ucCollage.cs`) exists on this machine**. Verify on-site:
>   compositing (Pos = centre, size = crop × Scale × Zoom, crop→scale→mirror→place, JPG out),
>   file matching (serial with `_` after char 12 + all KeyName keywords in filename), and the
>   image delete/backup logic.
> - Ships **safe by default** in `Harry.ini`: `Collage_Generate=false`, `DeleteAfterCollage=false`,
>   and **`DeletePictures=false`** (backup-then-delete, so no image is lost before verification).
> - **The channel-2 ACK timing must be checked under load** (450 ms budget): the part-exit
>   sequence logs a WARNING if a part exceeds 450 ms; watch the "Part-exit timing" line.

Phase numbers follow the execution order used in this project (SPS server was
brought forward; numbering otherwise tracks CLAUDE.md section 18).

---

## Completed Phases

| Phase | Scope | Key files |
|-------|-------|-----------|
| 1 | Skeleton, DI, config, logging | App.xaml.cs, IniConfigManager, IConfigService/IniConfigService, ILogService/SerilogService, AppConfig, CameraConfig |
| 2 | JSON loader, DB schema, repository, partitioning | JsonTemplateLoader(+models), DatabaseSchema, MySqlRepository, PartitionManager, IDatabaseService/MySqlDatabaseService, MeasurementDefinition, SettingDefinition |
| 3 | Camera TCP client + telegram parser | TelegramParser, TcpCameraClient, CameraTelegramEventArgs, CameraTelegramTypes, ParsedTelegram, Measurement/SettingSample, ICameraService/CameraConnectionService |
| 4 | Measurement pipeline | MeasurementProcessor, MeasurementRowBuilder, MeasurementDefinitionCache, PendingMeasurement |
| 5 | SPS server (7 channels) | TcpSpsServer, ISpsServer, SpsChannel, SpsPartExitData |
| 6 | Settings / Diagnostic / Part-Exit consumers | SettingsProcessor(+SettingDefinitionCache, PendingSetting), DiagnosticProcessor(+CsvFileWriter), PartExitProcessor |
| 7 | Main CSV export | CsvExportService (two-row header, R_/V_ columns) |
| 8 | Image cleanup + DB partition retention | ImageCleanupService |
| 9 | Collage generator | ICollageService/CollageService, CollageIniReader, CollageComposer, CollageLayout(+CollageImageSpec) |
| 11 | WPF dashboard UI (MVVM, dark theme) | MainViewModel, CameraViewModel, SpsChannelViewModel, Msa{,Module,Runs}ViewModel, Controls\uc{Camera,SpsChannel,Msa,Database,Csv}Control, Themes\DarkTheme.xaml |
| 10 | MSA engine | MsaCalculator, MsaService, MsaModels, BaseId, MsaReference(+Loader), MsaConfig |
| 8b | Health reporting on SPS KeepAlive + flush data-loss hardening | ISystemHealth/SystemHealthService, HealthSources, FlushHelper |
| 11b | Part-exit orchestration (parallel + ACK) + UI extensions | PartExitOrchestrator, ImageHandler, LimitSample schema, Overview/Log tabs |

---

## Part-exit orchestration + UI extensions (Phase 11b)

Implemented from the written spec (no V1 source on the machine to port literally).

1. **LimitSample schema** — `msa_results` gained `expected_value`/`actual_value` (VARCHAR(50));
   the startup ADD-COLUMN check applies them automatically (verified live). LimitSample tab
   now shows MeasurementName | Expected | Actual | Pass/Fail.
2. **Overview tab** (first tab) — cameras online/offline at a glance, today's OK/NG counts
   (from `dmcserial`, 5 s), last part exit, active order, active errors.
3. **Log tab** — in-memory ring buffer (max 1000); level toggles ALL/INFO/WARNING/ERROR;
   multi-select source toggles CAMERA/SPS/DATABASE/CSV/COLLAGE/MSA/SYSTEM; colour coding
   (white/amber/red); Export to `LogFilePath\Log_Export_yyyy-MM-dd_HH-mm.txt`. Serilog
   daily rolling files now retained **30 days**.
4. **Part-exit parallel sequence** — `PartExitOrchestrator` replaces the decoupled queues for
   channel 2. Saves `dmcserial`, then `Task.WhenAll`: OK → CSV ‖ Collage(if `Collage_Generate`)
   ‖ Images; NG → CSV ‖ Images. Each task timed (Stopwatch). ACK
   `serial.PadRight(32,'0') + ";" + true|false + "\r\n"` after all complete; `true` only if
   every task succeeded. Images for OK parts wait (untimed) for the collage to read first
   (no delete/compose race). 450 ms budget — a WARNING is logged if exceeded.
   - **Image handling:** search `Collage_SingleImages` recursively for `*.bmp` matching the
     serial (`_` after char 12, SZID + trimmer); `DeletePictures=true` → delete, else copy to
     `BackupFolder\YYYY\MM\DD\HH\`, verify size, delete source.
   - **Collage:** match by serial + all KeyName keywords; output `serial_Collage.jpg` to
     `Collage_ResultImages`. (Old NAS keys superseded for this flow.)
   - Timing shown on the CSV and Collage tabs: "Last export: CSV Xms | Collage Xms | Images Xms | Total Xms".
   - **Collage tab** (`ucCollageControl`): shows the last 4 generated collages as thumbnails
     (Queue<BitmapImage>(4), loaded OnLoad + Frozen off-thread, collection updated via
     Dispatcher.Invoke) in a horizontal WrapPanel with each image's timestamp below it.
5. **CSV rotation** — already correct (order-name change + `DataSetsPerFile`); now driven
   per-part synchronously via `ICsvService.WritePartAsync`.
6. **MSA navigation** — "Run X of Y" + Prev/Next, loads the latest run on startup, run
   timestamp + PASS/FAIL badge (built in Phase 11; wording aligned).

New INI keys: `[NAS] DeletePictures`, `BackupFolder`; `[Collage] Collage_SingleImages`,
`Collage_ResultImages`.

---

## Health / Alive reporting (Phase 8b)

The SPS KeepAlive channel (ch 1) now reports the real system state, not just camera
connectivity. Previously every flush failure was swallowed in a catch and the SPS
saw "all good" — the software ran blind.

- **Central registry** `ISystemHealth` (singleton): `Report(source, severity, message, ttl?)`,
  `Clear(source)`, `Snapshot()`. Thread-safe, lock-free reads. Transient events use a
  TTL (auto-expire); state faults stay until cleared.
- **KeepAlive wire format** (agreed with customer):
  `<mirror>;<cam1>;…;<camN>;<SIGNALWORD>[;<plain-text>]`
  - healthy → `…;OK` (no text)
  - warning → `…;WARNING;<text>`   (running but degraded)
  - error   → `…;ERROR;<text>`     (data loss / standstill imminent)
  - worst severity wins the word; messages joined with ` | `; text sanitized of `;`/CR/LF.
  - Camera 0/1 list keeps offline cameras visible **without** flipping the signal word.
- **Report points wired** (previously silent catches): DB unreachable/startup-failed
  (`MySqlDatabaseService`), flush exceptions + queue depth (Measurement/Settings/PartExit/
  Csv/Msa). DB up / flush ok / queue drained → `Clear`.
- **DB connectivity heartbeat:** once Ready, `MySqlDatabaseService` pings MySQL every 5s
  and owns the `Database` health source, so a *steady-state* outage **and its recovery**
  self-heal (KeepAlive returns to OK on its own) without needing production traffic.
  Each pipeline also clears its own fault when its queue is empty (no pending writes).
- **Severity policy:** ERROR = DB down, flush failing, queue full (dropping). WARNING =
  single rejected row, queue filling (≥50%), settings drop.

### Flush data-loss hardening (`FlushHelper.WriteAsync`)
A failed batch INSERT used to lose every already-dequeued row, and one poison row killed
the whole batch. Now: try batch → on failure retry row-by-row → a single bad row is
isolated (dropped + WARNING) while the rest land → ≥10 consecutive failures ⇒ DB assumed
down ⇒ remaining rows **requeued** (no loss) + sticky ERROR. Applied to Measurement,
Settings, PartExit, MSA; CSV uses an equivalent requeue-on-failure path.

A `Health: OK/WARNING/ERROR` indicator was added to the MainWindow status bar.

---

## Runtime State of Each Service / Processor

| Component | State | Runs on | Queue |
|-----------|-------|---------|-------|
| MySqlDatabaseService | ✅ | startup background Task | — |
| MySqlRepository / PartitionManager | ✅ | caller's Task | — |
| TcpCameraClient ×N | ✅ | one Task per camera (+ keepalive Task) | — |
| MeasurementProcessor | ✅ | dedicated Task | ConcurrentQueue |
| SettingsProcessor | ✅ | dedicated Task | ConcurrentQueue |
| DiagnosticProcessor | ✅ | dedicated Task | ConcurrentQueue (→ CSV, no DB) |
| PartExitOrchestrator | ✅ | channel-2 receive task (awaits the per-part pipeline) | — |
| CsvExportService | ✅ | called per part (SemaphoreSlim-serialized) | — |
| TcpSpsServer | ✅ | one Task per channel + per client | — |
| ImageCleanupService | ✅ | daily background Task (retention only) | — |
| ImageHandler | ✅ | called per part (Task.Run) | — |
| MsaService | ✅ | dedicated Task (storage) + per-eval Task | ConcurrentQueue (→ msa_measurements) |
| CollageService | ✅ | called per part (Task.Run, GDI+) | — |

All processors: receive threads only enqueue; per-operation MySqlConnection
(never shared); `ConfigureAwait(false)` on every async I/O op.

---

## Data Flow (current)

```
Cameras ─Results(Normal)──▶ MeasurementProcessor ─▶ measurements_serial(_trimmer)   [R_/V_ combined into one row]
        ─Results(MSA)─────▶ MsaService(storage)   ─▶ msa_measurements
        ─Settings─────────▶ SettingsProcessor     ─▶ settings
        ─Diagnostic───────▶ DiagnosticProcessor   ─▶ Diagnostic CSV
SPS Ch2 ─PartExit─▶ PartExitOrchestrator:
                     1. save dmcserial
                     2. Task.WhenAll  (OK: CSV ‖ Collage[if enabled] ‖ Images)
                                      (NG: CSV ‖ Images)            [≤450ms budget]
                     3. ACK  serial.PadRight(32,'0');true|false\r\n
SPS Ch3-7 ─Request;BaseID─▶ MsaService.HandleMsaRequest ─▶ Wait → (eval) → OK/NG
                                                          └▶ msa_results + MSA CSV
Daily ─▶ ImageCleanupService ─▶ delete aged NG/Diag/Golden images + DROP old partitions
```

---

## MSA engine notes (Phase 10) — assumptions to verify

- **Tolerance (USL−LSL)** is taken from the latest Min/Max in `settings` matched by
  **(camera_id, parameter_set)**. Confirm that parameter_set groups each feature's limits.
- **Reference value xm** (MSA1) and **prepared-error flags** (LimitSample) come from
  `MSA_<module>.json` in `[MSA] ReferencePath`, keyed by measurement **display_name**.
- **Evaluation is poll-based**: first `Request;<BaseID>` returns `Wait` and starts a
  background evaluation; later requests return `OK`/`NG` (or `Error;…`).
- MSA math (Cg, Cgk, %Tolerance, LimitSample) is in `MsaCalculator` and unit-verified.

---

## Collage notes (Phase 9) — assumptions to verify against a V1 collage

GDI+ (`System.Drawing.Common`, Windows-only) composition on a background task.

- **Trigger:** Part Exit with `Result = OK` and a non-MSA mode. NG/MSA parts skipped.
- **File match:** real filenames don't equal the `TemplateName` (variable serial + result
  digit in the middle), so a source file matches when its name **starts with** the 12-char
  serial prefix (SZID or VirtualSerial) **and ends with** the suffix after
  `<serial_pattern>`. Searched with an OS-level `prefix*` filter, recursively.
- **Compositing assumptions:** Pos_X/Pos_Y = image **centre**; draw size = crop × Scale ×
  Zoom; order crop → scale → mirror → place; images drawn in [ImageN] order (later on top).
  BackgroundColor accepts a name, `#RRGGBB`, or `R,G,B`.
- **Output:** `<SZID>_collage.png` written to `[NAS] CollagePath` (NAS auto-sorts into
  date folders). Output format follows the extension (png/jpg/bmp).
- **DeleteAfterCollage:** when set, the exact source files placed into the collage are
  deleted (consumed OK images, section 11).
- **Health:** collage faults are WARNING-level (non-critical artifact), with TTL.
- `Collage.ini` path now resolves relative to the config dir (portable), like Templates/MSA.

---

## UI notes (Phase 11)

Dark theme (`#1A1D23` bg, purple `#6B21A8` accent) via `Themes/DarkTheme.xaml` (implicit
styles, merged in App.xaml), **runtime-switchable to a light theme** via `ThemeManager`
(top-bar toggle button; see the "Suite-wide light/dark theming" section below). App icon (`HarrySuite_Icons\HarryDataServer.ico`) + CustomHelp
logo (top-left, 32px, embedded WPF resource). MVVM via CommunityToolkit.Mvvm; one 1 s
UI-thread `DispatcherTimer` drives all refresh (row counts every 30 s) — background events
(SPS activity, log) captured into thread-safe buffers, synced on the tick.

- **Top bar:** logo · app name · version + system-status LED. **Status bar:** status,
  error/warning count, uptime, health message, config file.
- **Cameras tab:** `ucCameraControl` per camera in a WrapPanel (dynamic from INI) — JSON/
  Connected/Auto-reconnect LEDs, IP:Port, telegram counter, last 3 telegrams (Queue, no DB),
  Reconnect button (`TcpCameraClient.RequestReconnect`).
- **SPS tab:** `ucSpsChannelControl` ×7 — connected LED, port, message counter, last 2
  requests + last 2 responses (fed by `ISpsServer.ChannelActivity`).
- **MSA tab:** `ucMsaControl` — 5 module sub-tabs × {MSA1, MSA3, LimitSample}, each a
  DataGrid loaded from `msa_results` (`IMsaService.GetRunsAsync`), Prev/Next run navigation,
  run datetime, PASS/FAIL badge, Export CSV. LimitSample shows pass/fail only (Expected/
  Actual are not persisted in `msa_results`).
- **Database tab:** `ucDatabaseControl` — connection + tables-initialized LEDs, per-table
  approximate row counts (INFORMATION_SCHEMA, 30 s), retention info.
- **CSV tab:** `ucCsvControl` — active file path, rows written, last write time.
- **Log tab:** live tail from an in-memory Serilog sink (`InMemoryLogSink`/`ILogBuffer`).
- Backend added for the UI: camera telegram counter + `RequestReconnect`/`JsonLoaded`/
  `AutoReconnectActive`; SPS per-channel activity event + connection counts; CSV active
  path/last-write; `IDatabaseService.GetRowCountsAsync`; MSA read path; in-memory log sink.
- Smoke-tested: launches, window renders (title "HarryDataServer V2"), dark theme + logo
  load, no startup crash. Visual polish can be iterated live on the server.

---

## Companion tools (Phase 14) — built

All 5 companion tools are implemented as separate WPF projects in the same solution
(`HarryDataServer.sln`), sharing a new **`HarryShared`** class library. Full solution
builds **0 warnings / 0 errors** (`net8.0-windows`). See `COMPANION_TOOLS.md` for the
per-tool spec and the on-site live-test checklist.

| Project | Scope | Key files |
|---------|-------|-----------|
| HarryShared | Shared library: dark theme, converters, CustomHelp logo, `HarryConfig` (Harry.ini → read-only **GetData** connection), `QueryService` (all read queries), `MsaReferenceFile`, `CsvExport` | Themes/DarkTheme.xaml, Converters/CommonConverters.cs, Config/HarryConfig.cs, Data/{QueryService,Models,MsaReferenceFile,CsvExport}.cs |
| HarryAnalysis | Scan DMC/serial → dmcserial header + all measurements (value/limits/result) in a grid; export CSV | MainViewModel.cs, MainWindow.xaml |
| HarryGraph | Multi-select definitions → OxyPlot time-series; Live (1 s rolling) / fixed range; save/load config JSON; print | MainViewModel.cs, DefItem.cs, MainWindow.xaml |
| HarryCounter | NG counts over a range grouped by feature_group / measurement / nest/module/order; grid + bar chart + yield; 5 s live; CSV | MainViewModel.cs, MainWindow.xaml |
| HarryLimitSample | Scan DMC → mark each measurement Should Pass/Fail/Ignore per module → save `MSA_<module>.json` (limit_sample_expected); preserves references (xm); load/edit existing | MainViewModel.cs, LimitSampleRow.cs, MainWindow.xaml |
| HarryCollageCreator | Visual Collage.ini editor: add BMPs, place/zoom/crop/mirror, **live GDI+ composite matching `CollageComposer`**, reorder, open/save Collage.ini, export preview PNG | MainViewModel.cs, ImageSlot.cs, CollagePreviewRenderer.cs, CollageIniIo.cs, MainWindow.xaml |

Conventions reused (per `COMPANION_TOOLS.md` §2): `net8.0-windows` + WPF, dark theme
shared from `HarryShared` (`pack://…/HarryShared;component/Themes/DarkTheme.xaml`),
CustomHelp logo + per-app `.ico`, CommunityToolkit.Mvvm, MySqlConnector with one
pooled connection per operation + `ConfigureAwait(false)`, read-only **GetData**
account (Server/Database from Harry.ini; `GetUser`/`GetPassword` overridable, default
GetData/1234Get). The customer-owned JSON templates were not touched.

> Build smoke: full solution `dotnet build -c Release` → 0/0; each tool produces its
> `.exe`. Live verification (real DB with data) is part of the on-site checklist.

### UX review pass 1 (2026-06-20)

Customer feedback applied across the tools (`fix: companion tools UX review pass 1`):

- **Shared theme** — templated `ComboBox` so the drop-down popup is dark (`#22262F`) with a
  readable `ComboBoxItem` style (light `#E2E8F0` text, blue `#3B82F6` selection, slate
  `#334155` hover). Fixes the low-contrast dropdowns in **all** tools.
- **HarryAnalysis — reworked.** Search now matches DMC **or** SZID **or** VirtualSerial and
  shows which field hit. Split layout: top = in-memory **scan history** (last 20: Timestamp |
  DMC | SZID | Result, colour-coded; click to load; Clear All; right-click → remove); bottom =
  detail (header card + measurement grid). Measurement grid is **sortable** (click headers,
  numeric Value/Min/Max). Export uses a `SaveFileDialog` defaulting to `[CSV] CSV_BasePath`
  with `Analysis_<DMC>_<yyyy-MM-dd>.csv`.
- **HarryCounter — multi-level tree.** Replaced the bar chart with a `TreeView` built from a
  recursive `ErrorTreeNode` hierarchy (ported from the RazorErrorCount pattern — *no original
  source was found on the machine, implemented from the spec*). Grouping order is chosen via
  three ComboBoxes (Feature Group / Measurement / M1x·M3x·M50 Nest / none); the deepest level
  is an OK/NG result breakdown. Title format `"<Key> – <Count>"`. New shared query
  `GetErrorTreeRowsAsync` (DB-side aggregation, folded client-side). OxyPlot dropped from this tool.
- **HarryGraph — multiple windows + UX.** 1–6 graph panels in a responsive `WrapPanel` (`+`/`–`
  buttons; each panel its own measurement ComboBox); a `↗` button opens a panel full-screen in
  its own live window. Dark **tracker tooltip** (`#1E2128`/`#E2E8F0`/`#3B82F6`, 12px). Zoom is
  **X-only** by default with a **Lock Y** toggle (Y auto-fits the data extent) and a **Reset
  Zoom** button. **Show limits** draws the time-varying Min/Max envelope **point-by-point**
  (dashed red `#EF4444`, 0.5px) using `GetLimitHistoryAsync` (latest setting ≤ each timestamp),
  so limit changes render as steps.
- **HarryLimitSample.** The full save path is shown under the Save button
  (`Saving to: …\MSA_<module>.json`, from `[MSA] ReferencePath`). The Expected column is now
  pre-populated from each measurement's result (1→ShouldPass, 0→ShouldFail, else Ignore),
  still individually editable.
- **HarryCollageCreator** — unchanged this pass (per request; revisit on-site with real images).

### UX review pass 2 (2026-06-20)

Second round of customer feedback (`fix: companion tools UX review pass 2`):

- **Shared theme** — added readable dark **ContextMenu/MenuItem** (was grey-on-grey) and
  **DatePicker / DatePickerTextBox / Calendar** styles (fixes the grey-in-grey date fields in
  HarryGraph + HarryCounter).
- **HarryAnalysis** — added **Export All** (one CSV across every scanned part, with leading
  part-identity columns) next to Clear All; single-part Export still available.
- **HarryCounter** — **bar chart is back**, now shown beside the tree (top-level groups' NG
  counts); OxyPlot re-added.
- **HarryGraph** — each panel is **multi-measurement again** (checkable popup list with filter,
  one colour per series + matching dashed envelope); the **detail window is a normal resizable
  window** (title-bar maximize/restore, no longer force-maximized; its own ↗ button hidden);
  panels **re-layout on app maximize/restore** (StateChanged + dispatched recompute).
- **HarryCollageCreator** — **Open/Save no longer throw** `ArgumentException` ("Value does not
  fall within the expected range"): the dialog `InitialDirectory` is only set when it points at
  an existing directory.
- **HarryLimitSample** — **Save no longer crashes** on duplicate `display_name` (same
  measurement on several cameras). Entries are keyed by display_name (server requirement), so
  duplicates collapse to one entry — **ShouldFail wins** and any mixed-mark conflicts are
  reported in the status line.

> **LimitSample reference model:** one file per module, `MSA_<module>.json`, in
> `[MSA] ReferencePath`. Each module's file accumulates entries keyed by measurement
> `display_name`. To **remove** an entry: *Load existing* → set its row to **Ignore** → *Save*
> (Save writes only the non-Ignore rows, replacing the module's `limit_sample_expected`); the
> `references` (xm) block is preserved.

> Phase 12 (MSA UI) was folded into the Phase 11 build (`ucMsaControl`).
> HarryMSA is therefore done (integrated tab); HarrySimulator is the customer's own tool.

---

## Database index audit + startup index-check (2026-06-20)

Performance audit of all 9 tables (`SHOW INDEX` + `EXPLAIN` on the typical
HarryAnalysis / HarryGraph / HarryCounter / MSA queries). Seven missing indexes were
found and created on the live DB, and the schema/startup code was updated so they are
applied automatically on every install.

**Indexes added** (live DB + `DatabaseSchema.cs` CREATE statements):
- `dmcserial.idx_result_status (result_status)` — HarryCounter NG-in-range scans.
- `measurements_serial.idx_serial_measured (serial_number, measured_at)` — by-serial
  lookup ordered by time without a filesort (HarryAnalysis).
- `measurements_serial.idx_def_measured (definition_id, measured_at)` — HarryGraph
  time-series, range + order served by one index.
- `measurements_serial_trimmer.idx_trimmer_measured (serial_trimmer, measured_at)`.
- `measurements_serial_trimmer.idx_def_measured (definition_id, measured_at)`.
- `msa_results.idx_base_id (base_id)` — lookup of one MSA run (was a table scan).
- `msa_results.idx_controller_type_eval (controller_name, msa_type, evaluated_at)` —
  per-module run navigation.

EXPLAIN confirms the new indexes are used (`idx_base_id`, `idx_result_status`); the two
measurement composites remove the `ORDER BY measured_at` filesort (verified with
`FORCE INDEX` — the cost-based optimizer adopts them once the tables hold real data).

**Startup index-check** — mirrors the existing ADD COLUMN schema-check. After the column
check, `MySqlDatabaseService.StartAsync` calls `MySqlRepository.EnsureIndexesAsync`, which
walks `DatabaseSchema.ExpectedIndexes`, checks each against `INFORMATION_SCHEMA.STATISTICS`,
and runs `CREATE INDEX` for any that are missing (logged at Information level). MySQL has
**no `CREATE INDEX IF NOT EXISTS`**, so the existence check substitutes for it. A new index
now deploys by a code change alone — no manual SQL, no production stop (CLAUDE.md §8).

---

## SOW compliance pass (2026-06-22)

Closed several SOW gaps from CLAUDE.md §19 and tightened filename/folder conventions.

1. **Collage 128 KB size limit (SOW §5.2.2)** — `CollageComposer` now enforces a max
   output size for JPEG collages: it re-encodes at decreasing quality (start 85, step −5,
   min 30) until the file fits, logging a WARNING if the minimum quality is reached and the
   file still exceeds the limit. Configurable via `[Collage] MaxFileSizeKB` (default 128).
2. **Filename datetime DDMMYY_HHMMSS (SOW §5.1.2)** — new `Infrastructure/FileNaming.cs`
   centralises the pattern (`ddMMyy_HHmmss`). Applied to main/MSA/diagnostic CSV
   (`CsvFileWriter`), the MSA-tab CSV export, the log export, and the companion-tool CSV
   exports (`HarryShared.Data.CsvExport`). MSA CSV files are labelled module + type.
3. **Backup folder structure (SOW §5.2.3)** — `ImageHandler` backup path is now
   `BackupFolder\YYYY\MM\DD\` (dropped the `\HH\` level).
4. **GSM folder names (SOW §1.2.1)** — exact constants in `FileNaming`:
   `Golden Sample Data`, `Golden Sample Images`, and the per-run subfolder
   `<TestType>_<DDMMYY_HHMMSS>_<Module>` (e.g. `MSA1_220626_143022_M50`). No magic strings.
5. **NG low-res deletion linkage (SOW §5.2.3)** — NG parts no longer delete their low-res
   individual images at part exit (`PartExitOrchestrator` NG branch is CSV-only now). Instead
   `ImageCleanupService`, when it deletes an aged full-res NG image, also deletes the matching
   low-res images (linked by the 12-char serial prefix). OK-part behaviour is unchanged
   (low-res consumed/deleted after collage).
6. **MSA PDF reports (SOW §3.2.1)** — new `IPdfReportService`/`PdfReportService` (QuestPDF
   2026.6.0, Community licence). After every MSA evaluation, `MsaService` writes two PDFs:
   `<Module>_<Type>_<DDMMYY_HHMMSS>_AllResults.pdf` and `_FailuresOnly.pdf`, to
   `[MSA] ReportPath` (fallback `[MSA] ReferencePath\Reports`). Layout: header (module/type/
   run datetime/overall PASS-FAIL), table (Measurement | Expected | Actual | Cg/Cgk or %P/T |
   Pass/Fail), footer (generated-by + timestamp + page). Registered Singleton in `App.xaml.cs`.
7. **PDF buttons in the MSA tab** — `ucMsaControl` gained **Open All Results** /
   **Open Failures Only** per run (plain `Button`, same implicit dark-theme style as the Log
   tab / Export CSV). They open the PDF in the default viewer, generating it on demand from
   the loaded run if it doesn't exist yet. Wired `IPdfReportService` through
   MainViewModel → MsaViewModel → MsaModuleViewModel → MsaRunsViewModel.
8. **Unified button style** — verified: every toggle/action button across the suite uses the
   implicit `Button`/`ToggleButton` styles (no per-button overrides anywhere). The companion
   tools consume `HarryShared/Themes/DarkTheme.xaml`; HarryDataServer's own theme defines the
   identical styles. New PDF buttons follow the same pattern.
9. **New INI keys** — `[MSA] ReportPath`, `[Collage] MaxFileSizeKB=128`,
   `[NAS] FullResRetentionDays=30` (the per-type NG/Diagnostic/GoldenSample retentions now
   fall back to `FullResRetentionDays` when unset). Added to `AppConfig`, `IniConfigManager`,
   and the `Harry.ini` template.
10. **MSA cycle count** — confirmed there is no hardcoded cycle count or INI key; `MsaService`
    aggregates all measurements for a BaseID regardless of count (SPS/PLC controls the count).
    The §19.4 verify note was updated to record this.

New files: `Infrastructure/FileNaming.cs`, `Services/{IPdfReportService,PdfReportService}.cs`,
`Models/MsaReport.cs`. Full solution builds **0 warnings / 0 errors**.

---

## BaseID 14-char format + MSA pipeline audit (2026-06-23)

The BaseID format changed: the old 19-char form (with TrayRow/TrayCol/Loop1-3) is gone.
BaseID is now **14 chars** `MMYYMMDDHHmmSS` (e.g. `10260623083000` = M10, 2026-06-23,
08:30:00). During a run each loop telegram appends a **3-digit loop counter** to the BaseID
in the serial field; the completion signal `Request;<BaseID>` carries the **bare 14 chars**.

1. **Telegram serial layout (MSA modes) — swapped + reformatted.** Serial field 1 (positions
   4–35) now carries `BaseID(14)+loop(3)`; serial field 2 (36–67) carries the DMC. New
   `BaseId.TrySplitRun` splits the run serial into the 14-char `base_id` + integer
   `loop_number`. `BaseId` rewritten to the 14-char model (Module/Year/…/Second; Tray/Loop
   fields removed). `MsaService.OnResultsReceived` updated accordingly; `ParsedTelegram`
   doc-comments corrected. (`Models/BaseId.cs`, `Models/ParsedTelegram.cs`,
   `Services/MsaService.cs`)
2. **Storage separation verified.** `msa_measurements.base_id` stores **only** the 14-char
   BaseID; the 3-digit counter goes to `loop_number` (int). Confirmed in the insert path.
3. **Schema / auto-migration.** Added composite index
   `msa_measurements.idx_baseid_controller (base_id, controller_name)` to the CREATE +
   `ExpectedIndexes` so the startup index-check applies it to existing DBs too; column
   comments clarified (`base_id` = 14-char, never with loop). (`Infrastructure/DatabaseSchema.cs`)
4. **Completion handler.** `Request;<14-char BaseID>` → aggregate `msa_measurements` on an
   **exact** `base_id = @x` match (never LIKE/prefix), additionally scoped by
   `controller_name LIKE '<module>%'` (one run = one module = one msa_type). (`MsaService.GatherAsync`)
5. **MSA calculation audited.** Cg/Cgk (MSA1, pass ≥1.33), %Tolerance (MSA3, pass ≤20%),
   LimitSample (100% rejection) confirmed against the SOW. `MsaCalculator.Msa3` computes
   DoF **dynamically** as Σ(measurementsPerPart−1) — it does **not** assume a fixed cycle
   count, so it is correct for any number of parts/loops (e.g. 30×3 → 60 or 32×3 → 64). The
   evaluation aggregates all loops for a base_id regardless of count.
6. **MoverNumber.** No `Mover`/`MoverNumber`/`MoverNr` references exist in the solution
   (verified by search) — nothing to remove on our side. (Keyence Fieldbus byte 66 is the
   controller's concern, not ours.)
7. **MSA-vs-production routing hardened.** `MeasurementProcessor` (Normal-only) and
   `MsaService` (MSA-only) already split cleanly. **Fixed a leak:** `PartExitOrchestrator`
   previously wrote `dmcserial` *before* the MSA check — the MSA guard now runs first, so MSA
   test parts never touch production tables (CSV/Collage/Images skipped too). MSA data →
   `msa_measurements` + `msa_results` only. (`Services/PartExitOrchestrator.cs`)
8. **PDF reports verified.** Both on-eval and on-demand generation build from the base_id
   aggregation; the on-demand path (`MsaRunsViewModel` → `MsaReportData.FromRun`) uses the
   stored 14-char base_id — no length assumptions. (`Models/MsaReport.cs`, `Services/MsaService.cs`)

Docs updated: `CLAUDE.md` §4/§5/§6/§7/§8, `DATABASE_SCHEMA.md` (msa_measurements/msa_results).
Full solution builds **0 warnings / 0 errors**.

---

## Image naming, MSA result folders, CSV structure, cleanup fix (2026-06-23)

1. **Hyphen image filename format.** New `Infrastructure/ImageFileName.cs` parses the Keyence
   filename `<Field1 32>-<Field2>-<overall>-<Controller>-<Nest>-&<ImageName>.png` (same structure
   Normal/MSA; only Field1/Field2 differ). Separator is `-` (SZID has `_` after char 12). Field1 =
   SZID (Normal) / BaseID(14)+Loop(3)+padding (MSA); Field2 = zeros / DMC. Field1 read up to the
   first `-`; Field2 (DMC, may contain `-`) recovered by anchoring on the `&ImageName` tail.
   Helpers: `MatchesBaseId(14)`, `MatchesSerialPrefix(12)`, `SortedRoot`.
2. **Per-run MSA result collection.** `Infrastructure/MsaResultLayout.cs` resolves
   `<ResultPath>\YYYY\MM\DD\<BaseID>\{PDF,CSV,IMG}` (date from the BaseID timestamp). On run
   completion `MsaService` now: writes the MSA CSV into `…\CSV\`, the 2 PDFs into `…\PDF\`
   (`PdfReportService`), and **moves** the GoldenSample run images (Field1 starts with the 14-char
   BaseID) into `…\IMG\`. New `[MSA] ResultPath`; **removed** `[MSA] ReportPath` and `[CSV] CSV_MSAPath`.
3. **CSV structure (verified).** Production (`CsvExportService`) and diagnostic (`DiagnosticProcessor`)
   CSVs already write directly into `CSV_BasePath\YYYY\MM\DD` / `CSV_DiagnosticPath\YYYY\MM\DD`
   (`dateSubfolders:true`) — confirmed, no change needed.
4. **ImageCleanupService — search SORTED folders.** Rewritten: iterates `YYYY\MM\DD` day-folders
   under the **sorted root** (parent of each `\Input`), takes age from the **folder name** (not file
   timestamps), and deletes whole day-folders past retention. Applies to NG (+ linked low-res by
   12-char prefix), Diagnostic, GoldenSample, **and Collage**. New **`[NAS] RetentionCollageDays`**
   (separately adjustable; falls back to `FullResRetentionDays`). Partition-drop unchanged.
5. **LimitSample reference path sync (verified).** HarryLimitSample reads/saves
   `MSA_<module>.json` via `HarryConfig.MsaReferencePath` (`[MSA] ReferencePath`); the server reads
   `MsaConfig.ReferencePath` (same key, same central Harry.ini). No hardcoded path. `ReferencePath`
   (input) and `ResultPath` (output) are distinct folders.
6. **Harry.ini + docs.** Template updated (removed `CSV_MSAPath`/`ReportPath`, added `ResultPath` +
   `RetentionCollageDays`, documented the hyphen filename format and the ResultPath subfolder
   layout). CLAUDE.md §10/§11/§13 updated.

New files: `Infrastructure/{ImageFileName,MsaResultLayout}.cs`. Config: `MsaConfig.ResultPath`
(replaces `ReportPath`), `NasConfig.RetentionCollageDays`, `CsvConfig.MsaPath` removed.

---

## Suite-wide light/dark theming (2026-06-29)

A runtime **light/dark theme switch** was added across the whole suite (all 6 WPF apps).

1. **`ThemeManager`** — `HarryDataServer/Theming/ThemeManager.cs` (server dashboard) and
   `HarryShared/Theming/ThemeManager.cs` (the 5 companion tools), same logic. It mutates the
   palette `SolidColorBrush` instances held in the application resources **in place**, so every
   `DynamicResource` consumer (views + implicit styles) updates **live** without reloading any
   window. `AppTheme` enum = `{Dark, Light}`.
2. **Palette** — per-key (dark ARGB, light ARGB) pairs: `WindowBg`, `PanelBg`, `CardBg`,
   `BorderBrush`, `TextBrush`, `TextDimBrush`, `PopupBg`, `HoverBg`, `ItemText`. `Accent`
   (`#6B21A8`), `AccentLight` (`#8B5CF6`) and the semantic LED colours stay **constant** across
   both themes. Keys absent in a given app are skipped.
3. **DarkTheme.xaml (both projects)** — palette brushes the implicit styles reference were
   converted from `StaticResource` → **`DynamicResource`** so the in-place colour swap propagates.
   New surface keys `PopupBg`/`HoverBg`/`ItemText` added for popups/menus to read well in light mode.
4. **Persistence** — the chosen theme is written to `%LOCALAPPDATA%\HarrySuite\theme.txt`, so the
   choice is **shared across the whole suite** and survives restarts. Best-effort (falls back to
   Dark on any I/O error). Each window calls `ThemeManager.Initialize()` at startup.
5. **Toggle UI** — server: a `ThemeToggle` button in the top bar; companion tools: a per-app
   toggle button. Content flips between `☀ Light` / `🌙 Dark` (`OnThemeToggle` → `Toggle()` →
   `UpdateThemeButton()`). Default is Dark when nothing is saved.

### Theme toggle freeze-fix (2026-06-29)

The toggle button switched the persisted state but had **no visual effect** — the UI stayed
dark. Root cause: `ThemeManager.Apply` mutated each palette brush in place (`brush.Color = …`)
behind a `!brush.IsFrozen` guard, but the palette brushes are **frozen** once consumed by the
sealed implicit-style setters, so every mutation was silently skipped. (The `StaticResource →
DynamicResource` conversion of all palette consumers was already in place — verified: 0 palette
`StaticResource` references remain in any XAML.) Fix: `Apply` now **replaces** each resource with
a fresh `SolidColorBrush` (`app.Resources[key] = new SolidColorBrush(color)`); since consumers use
`DynamicResource`, they re-resolve to the new brush and the UI updates live. Applied to **both**
`HarryDataServer/Theming/ThemeManager.cs` and `HarryShared/Theming/ThemeManager.cs`. **Verified
live on the server (2026-06-29):** the toggle switches dark⇄light instantly across all apps and
the choice persists across restarts.

New files: `HarryDataServer/Theming/ThemeManager.cs`, `HarryShared/Theming/ThemeManager.cs`.
Touched: both `Themes/DarkTheme.xaml` + all 6 `MainWindow.xaml`/`.xaml.cs` (server top bar +
`HarryGraph/GraphPanelControl.xaml`/`GraphWindow.xaml`). CLAUDE.md §14 documents the mechanism.

---

## Serial field rework — 22-char Serial1 (2026-06-29)

Serial1 (positions 4–35) is transmitted as 32 chars but only the first **22 are meaningful**
(SPS agreement); Serial2 (36–67) stays **32 chars**. Serial1 is now truncated to 22 at parse
time and the DB serial columns are `VARCHAR(22)`, so stored values match the 22-char Field 1 of
the Keyence image filenames (collage / MSA image collection / cleanup).

1. **Parse-time truncation.** `TelegramParser.ParseLine` caps Serial1 to 22 chars (single
   chokepoint); Serial2 untouched. New `ParsedTelegram.Serial1MaxLength = 22` + updated doc-comments.
2. **Shared width + guard.** New `Infrastructure/SerialField.cs` (`MaxLength = 22`, `Cap()`): the
   single definition of the width, used by the parser, the image-filename parser, and the inserts.
3. **DB inserts.** `PartExitOrchestrator.SaveDmcAsync` caps `serial_number`/`serial_trimmer` to 22
   and logs a WARNING on truncation (the part-exit telegram comes from the SPS, not the camera
   parser, so this is the enforcement point). `MeasurementProcessor` caps defensively.
4. **Schema → VARCHAR(22).** All four Serial1 columns: `dmcserial.serial_number`,
   `dmcserial.serial_trimmer`, `measurements_serial.serial_number`,
   `measurements_serial_trimmer.serial_trimmer` (`dmc` stays VARCHAR(50) = Serial2).
   `msa_measurements.base_id` unchanged (14-char BaseID).
5. **Migration = DROP + CREATE (not ALTER).** Tables are disposable in trial operation, so
   `MySqlRepository.RebuildOutdatedSerialTablesAsync` checks each serial column's
   `CHARACTER_MAXIMUM_LENGTH` at startup and, only on a width mismatch, **drops and recreates** the
   table at the new width (logged WARNING; data cleared). Idempotent — fires once on transition.
   Runs after `EnsureTables`, before the ADD-COLUMN check; index/partition checks repopulate the
   recreated tables. Driven by `DatabaseSchema.SerialColumnWidths`.
6. **Image filename.** `ImageFileName` Field 1 capped to 22 chars (`Field1Of` + `TryParse`); format
   documented as `<Field1 22>-<Field2 32>-…`. MSA run-image collection keeps matching on the
   **14-char BaseID prefix** (`MatchesBaseId`, used by `MsaService.MoveRunImages`) so all loops are
   gathered regardless of the 22-char Serial1; NG cleanup linkage stays at the **12-char** prefix.
7. **Routing (verified, unchanged).** M2X → `measurements_serial_trimmer`, M1X/M5X →
   `measurements_serial`, keyed on the camera's INI `Module` in `MeasurementProcessor` (comment
   clarified). Already correct — left in place per the spec's "leave alone if correct" note.

New file: `Infrastructure/SerialField.cs`. Touched: `TelegramParser`, `ParsedTelegram`,
`MeasurementProcessor`, `PartExitOrchestrator`, `DatabaseSchema`, `MySqlRepository`, `ImageFileName`.
CLAUDE.md §4/§11 updated. Full solution builds **0 errors** (`net8.0-windows`).

---

## Camera telegram layout fix — real Keyence layout + mode/diagnostic UI (2026-06-29)

The telegram parser was built on an **assumed** layout (a single operating-mode string at token 3).
Against the **real** Keyence cameras token 3 is the first character of Serial1, so `ParseMode`
returned `Unknown` and **every telegram was dropped** — thousands of warnings, nothing reached the
DB. The real layout is now confirmed from the live "Datenausgabe" configs (M50_ST110, M11_ST030)
and `Result_Header.xlsx`: serials are **32 comma-tokens each**, then four boolean mode flags, then
the overall result, then measurements.

1. **Serial offsets corrected** (`ParsedTelegram`/`TelegramParser`). Serial1 = concat tokens
   **3–34** → truncate to 22 chars; Serial2 = concat tokens **35–66** → full 32. (Was 4–35 / 36–67,
   off by one.) The 22-char truncation chokepoint (`SerialField`) is unchanged.
2. **Operating mode from 4 boolean flags, not a string.** `ParseMode(Fields[3])` removed. New
   `TelegramParser.DeriveMode` reads the flags at tokens **68/69/70**: all 0 → `Normal`,
   `Mode_MSA1` → `Msa1`, `Mode_MSA3` → `Msa3`, `Mode_GoldenSample` → `LimitSample`. Only one is
   ever set; **>1 set → WARNING + `Normal`**.
3. **`Mode_Diagnostic` (token 67) is independent** of the operating mode. Exposed as the separate
   bool `ParsedTelegram.IsDiagnostic`; it has **no effect on processing/routing** (INFO only).
   ⚠️ **Superseded** — this section originally said "Diagnostic is no longer a signal word / the
   pipeline is dormant." That was wrong: a standalone **Diagnostic telegram** (different layout,
   word at token 65) is a separate thing and is fully handled — see the next section
   "Diagnostic telegram — detect by token, raw rotating CSV dump (2026-06-29)".
4. **`Total_Result` (token 71)** exposed as `ParsedTelegram.OverallResult` (SINT). Measurements
   start at token **72** and never consume token 71. It is **display only** — the authoritative
   OK/NG for collage/CSV/images still comes from the PLC at part-exit (logic untouched).
5. **Result templates shifted +1.** Every `telegram_place` in **all 14** `Result_*.json` moved
   71→72 (first measurement), so they match the new layout. `Settings_*.json` untouched (still
   from token 3). Verified: M50_ST110 now 72…287, 216 distinct/contiguous places.
   - **Deviation from the task brief:** the brief said the two new M10/M11 **ST030** templates were
     "already at 72, leave untouched". In fact **all four M1X templates** (M10/M11 × ST030/ST060)
     were TODO **stubs at 71**. Leaving them would have violated the DoD ("first measurement at 72")
     and made their `R_` entry read `Total_Result`, so they were shifted with the rest. They remain
     placeholder stubs (single `R_`/`V_` TODO pair) to be filled from camera docs.
6. **Camera control UI** (`CameraViewModel` + `ucCameraControl.xaml`). Each camera card now shows,
   updated **live per Results telegram**: the **operating mode** as status text
   (`Normal Operation` / `MSA1` / `MSA3` / `Limit`) and a **separate "Diagnose" badge** that
   highlights (Accent) when `IsDiagnostic` is true — independent of the mode. Both read the last
   telegram only; theme-aware via `DynamicResource`. The recent-telegram list also shows mode +
   IO/NG (`Total_Result`).
7. **Settings path verified (Task 5).** The Settings parser reads `telegram.Field(place)` from
   token 3 onward with no serial/mode block — matches `Settings_Header.xlsx` (token 2 = `Settings`).
   No change needed.

Touched: `Models/ParsedTelegram.cs`, `Communication/TelegramParser.cs`, `ViewModels/CameraViewModel.cs`,
`Controls/ucCameraControl.xaml`, all 14 `Resources/Templates/Result_*.json`. CLAUDE.md §4/§6 rewritten
to the real layout. Full solution builds **0 errors / 0 code warnings** (`net8.0-windows`; the only
warnings are environmental `NU1900` NuGet-audit offline notices).

---

## Diagnostic telegram — detect by token, raw rotating CSV dump (2026-06-29)

**Corrects the previous section's claim** that "Diagnostic is no longer a signal word / the
pipeline is dormant." That was wrong: there are **two unrelated things named diagnostic** and both
must work. This change fixes the first.

A **Diagnostic telegram** has a **different layout** from Results/Settings — no version field,
serials first, and the literal word `Diagnostic` at **token 65** (confirmed live, M50_ST140_KF1):
`ctrl, Serial1(1–32), Serial2(33–64), "Diagnostic"(65), label(66), arbitrary VAL_…`. The earlier
parser only recognised `Diagnostic` as a signal word at token 2, so these telegrams were never
detected.

1. **Detection by token scan, not position** (`TelegramParser`). `ParseLine` now scans the
   comma-split tokens for an exact `Diagnostic` token (case-insensitive, trimmed) **before** the
   signal-word dispatch; if found, `BuildDiagnostic` parses the diagnostic layout (Serial1 tokens
   1–32 → truncated 22; Serial2 tokens 33–64 → full 32; `DiagnosticStart` = index of the word) and
   returns `Signal = Diagnostic`. Results/Settings bodies are serials/numbers and never contain the
   word → no false positives. New `ParsedTelegram` members: `DiagSerial1/2*` layout constants +
   `DiagnosticStart`.
2. **Raw CSV dump** (`DiagnosticProcessor`, rewritten). One row per telegram, plain left-to-right:
   `ReceivedAt` (`ddMMyy_HHmmss` via `FileNaming`), **Serial1(≤22)**, **Serial2(32)**, then **every
   remaining token** from the `Diagnostic` word onward (word + label + all values) exactly as
   received. **Variable column count** per row (raw dump, no fixed schema/header). Queue of
   `string[]`; CSV-only, never the DB.
3. **File naming + rotation.** New `CsvFileWriter` `labelFirst` option →
   `Diagnostic_<ddMMyy_HHmmss>.csv` written **directly** in `[Diagnostic] DiagnosticPath` (no date
   subfolders), a **new** file every `[Diagnostic] MaxRows` rows (default 1000); existing files are
   never renamed/overwritten (same-second collision guard already present). Existing CsvFileWriter
   callers are unchanged (`labelFirst` defaults false).
4. **Config.** New `[Diagnostic]` section + `DiagnosticConfig` (`DiagnosticPath`, `MaxRows`). To
   avoid duplicating the path, `DiagnosticPath` **falls back to the legacy `[CSV] CSV_DiagnosticPath`**
   when empty; the enable flag stays on the existing `[CSV] CSVDiagnostic_Save`. Harry.ini template
   updated.
5. **`Mode_Diagnostic` flag unchanged.** Token 67 of a *Results* telegram (`ParsedTelegram.IsDiagnostic`
   + UI badge) is the unrelated INFO-only boolean — left exactly as is. `BuildDiagnostic` sets
   `IsDiagnostic = false` (it is the signal-word kind, not the flag).
6. **Reachability verified.** TCP → `TcpCameraClient.ProcessFrame` → `ParseLine` (token scan →
   `Signal=Diagnostic`) → `case Diagnostic` → `DiagnosticReceived` → `DiagnosticProcessor`
   (started in `App.xaml.cs`) → `Diagnostic_*.csv`. Not dead code.

**Implementation choice (documented):** the DoD example shows the file directly in `DiagnosticPath`,
so the dump is written **flat** (no `YYYY\MM\DD` subfolders, unlike the production CSV). Files stay
self-identifying via the timestamped name.

Touched: `Models/ParsedTelegram.cs`, `Communication/TelegramParser.cs`, `Services/DiagnosticProcessor.cs`,
`Infrastructure/CsvFileWriter.cs`, `Models/AppConfig.cs`, `Configuration/IniConfigManager.cs`, `Harry.ini`.
CLAUDE.md §4 gained the Diagnostic-telegram layout + the Diagnostic-vs-`Mode_Diagnostic` contrast.
Full solution builds **0 errors / 0 code warnings** (`net8.0-windows`; only environmental `NU1900`).

---

## Test-day helpers: OK/NG badge + raw telegram capture (2026-06-30)

Three test-day aids for the live line.

1. **OK/NG badge on telegram lines.** The camera control's "Last telegrams" list now leads each
   Results line with the overall result from `Total_Result` (token 71): `1 → OK`, `0 → NG`, else
   `?` — e.g. `09:21:14 | OK | Normal Operation | 0000…`. Display only (the authoritative OK/NG
   still comes from the PLC at part-exit). Plain text via `DynamicResource` brushes (theme-legible).
   (`ViewModels/CameraViewModel.cs`)
2. **Global raw telegram capture.** New `ITelegramCapture`/`TelegramCaptureService` (singleton,
   `IDisposable`) + a top-bar checkbox **"Telegramme mitschneiden"** (OFF by default, not
   persisted). While on, every incoming real telegram is written verbatim (before parsing) to
   `Capture\Capture_<Controller>_<ddMMyy_HHmmss>.csv` next to the exe — `<stamp>,<raw line>`, one
   file per controller, `AutoFlush`, lock-serialized across receive threads, flushed/closed on
   toggle-off or app exit. Hooked in `TcpCameraClient.ProcessFrame`. (New
   `Services/TelegramCaptureService.cs`; touched `TcpCameraClient`, `CameraConnectionService`,
   `MainViewModel`, `MainWindow.xaml`, `App.xaml.cs`, `Themes/DarkTheme.xaml` for a CheckBox style.)

---

## Warning noise reduction + capture/NoSerial hardening (2026-06-30)

Follow-up to the warning-count diagnosis (idle controllers inflated the cumulative warning counter;
the 1000-entry all-level Log buffer hid recent warnings under Debug chatter).

1. **Option 1 — per-outage logging (`TcpCameraClient`).** A controller going offline now logs a
   WARNING **once**, on the `Connected → Disconnected` transition (`_loggedOffline` flag, touched
   only on the RunAsync thread). Subsequent retries for an already-offline controller log at
   **Debug**; recovery logs one **Information** (`reconnected`). The keepalive "consecutive failed
   pings" line was demoted Warning → Debug (the disconnect it triggers already emits the single
   per-outage Warning). Net: one Warning per outage instead of ~1/min/offline-camera.
2. **Option 4 — dedicated Warning+Error ring (`InMemoryLogSink`).** Alongside the general
   all-level buffer (max 1000), a second buffer holds Warning+Error entries (max 1000). The same
   `LogEntry` instance is enqueued in both; `Snapshot()` merges them de-duplicated (by reference)
   and time-ordered. Recent warnings/errors now stay visible in the Log tab regardless of Debug
   volume. No `LogViewModel` change needed.
3. **Part B — keepalive excluded from capture (`TcpCameraClient.ProcessFrame`).** `MR,…`/`ER…`
   keepalive replies are no longer written to the capture files; only real Results/Settings/
   Diagnostic telegrams (incl. NoSerial) are captured. `IsKeepAliveReply` is evaluated once.
4. **Part C — NoSerial drop.** `ParsedTelegram.IsNoSerial` (Results with empty/all-zero Serial1).
   `ProcessFrame` skips raising `ResultsReceived` for such telegrams, so nothing reaches
   `MeasurementProcessor`/`MsaService` (no DB writes); it is logged WARNING, still captured (capture
   precedes the drop), and shown as **`NoSerial`** in the camera control (status text + telegram
   line). Results-only.

Touched: `Communication/TcpCameraClient.cs`, `Models/ParsedTelegram.cs`,
`ViewModels/CameraViewModel.cs`, `Services/InMemoryLogSink.cs`. CLAUDE.md §3/§4 updated.

---

## Build & Repo
- `dotnet build HarryDataServer.sln -c Release` → 0 warnings, 0 errors (`net8.0-windows`).
- Branch `main`, pushed to `https://github.com/CustomHelp/HarryDataServerV2`.
