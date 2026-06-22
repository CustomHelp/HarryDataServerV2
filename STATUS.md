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
styles, merged in App.xaml). App icon (`HarrySuite_Icons\HarryDataServer.ico`) + CustomHelp
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

## Build & Repo
- `dotnet build HarryDataServer.sln -c Release` → 0 warnings, 0 errors (`net8.0-windows`).
- Branch `main`, pushed to `https://github.com/CustomHelp/HarryDataServerV2`.
