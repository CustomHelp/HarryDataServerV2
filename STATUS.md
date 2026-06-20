# HarryDataServer V2 — Implementation Status

*Last updated: 2026-06-20*

> ## ⚠️ Pending on-site (live) verification
> - **Phase 9 — Collage generator** is implemented and compiles but has **not** been
>   tested against real images / a real V1 collage. The compositing semantics
>   (Pos = centre, size = crop × Scale × Zoom, crop→scale→mirror→place, PNG output)
>   are **assumptions** — verify them on-site and adjust `CollageComposer` if needed.
> - Ships **OFF by default** so go-live is safe: in `Harry.ini`,
>   `Collage_Generate=false` and `DeleteAfterCollage=false`. Enable both only after the
>   on-site collage output looks correct.
> - **When the customer goes live, the collage must be re-tested** before enabling it.

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
| 10 | MSA engine | MsaCalculator, MsaService, MsaModels, BaseId, MsaReference(+Loader), MsaConfig |
| 8b | Health reporting on SPS KeepAlive + flush data-loss hardening | ISystemHealth/SystemHealthService, HealthSources, FlushHelper |

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
| PartExitProcessor | ✅ | dedicated Task | ConcurrentQueue (→ dmcserial) |
| CsvExportService | ✅ | dedicated Task | ConcurrentQueue (→ main CSV) |
| TcpSpsServer | ✅ | one Task per channel + per client | — |
| ImageCleanupService | ✅ | daily background Task | — |
| MsaService | ✅ | dedicated Task (storage) + per-eval Task | ConcurrentQueue (→ msa_measurements) |
| CollageService | ✅ | dedicated Task | ConcurrentQueue (→ collage image + delete OK sources) |

All processors: receive threads only enqueue; per-operation MySqlConnection
(never shared); `ConfigureAwait(false)` on every async I/O op.

---

## Data Flow (current)

```
Cameras ─Results(Normal)──▶ MeasurementProcessor ─▶ measurements_serial(_trimmer)   [R_/V_ combined into one row]
        ─Results(MSA)─────▶ MsaService(storage)   ─▶ msa_measurements
        ─Settings─────────▶ SettingsProcessor     ─▶ settings
        ─Diagnostic───────▶ DiagnosticProcessor   ─▶ Diagnostic CSV
SPS Ch2 ─PartExit─┬▶ PartExitProcessor  ─▶ dmcserial
                  ├▶ CsvExportService   ─▶ main CSV (all measurements/part, 2-row header)
                  ├▶ CollageService (OK+Normal → compose collage, delete consumed OK images)
                  └▶ ImageCleanupService (NG → delete orphaned OK images)
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

## Not yet built

- **Phase 11/12** — WPF UI (per-subsystem UserControls, MSA view).
- **Phase 14** — Companion tools (HarryAnalysis, HarryGraph, HarryCounter, etc.).

---

## Build & Repo
- `dotnet build HarryDataServer.sln -c Release` → 0 warnings, 0 errors (`net8.0-windows`).
- Branch `main`, pushed to `https://github.com/CustomHelp/HarryDataServerV2`.
