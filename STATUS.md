# HarryDataServer V2 — Implementation Status

*Last updated: 2026-06-20*

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
   - Timing shown on the CSV tab: "Last export: CSV Xms | Collage Xms | Images Xms | Total Xms".
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

## Not yet built

- **Phase 14** — Companion tools (HarryAnalysis, HarryGraph, HarryCounter, etc.).

> Phase 12 (MSA UI) was folded into the Phase 11 build (`ucMsaControl`).

---

## Build & Repo
- `dotnet build HarryDataServer.sln -c Release` → 0 warnings, 0 errors (`net8.0-windows`).
- Branch `main`, pushed to `https://github.com/CustomHelp/HarryDataServerV2`.
