# HarryDataServer V2 — Implementation Status

*Last updated: 2026-06-19*

This file tracks what has actually been built, the runtime state of each component,
and the next planned step. Phase numbers below follow the **execution order used in
this project**, which diverges slightly from the numbered list in CLAUDE.md section 18
(the SPS server was brought forward ahead of the Settings/Diagnostic pipelines).

---

## Completed Phases

### Phase 1 — Solution skeleton, DI, configuration, logging
- `HarryDataServer.sln`, `HarryDataServer.csproj` (WPF .NET 8), `app.manifest`, `.gitignore`
- `App.xaml` / `App.xaml.cs` — DI container (all services singletons), startup orchestration, graceful shutdown
- `MainWindow.xaml` / `MainWindow.xaml.cs` — tab layout + status bar
- `Configuration/IniConfigManager.cs` — dynamic `[CameraN]` discovery, relative-path resolution against the INI directory
- `Services/IConfigService.cs` + `IniConfigService.cs`
- `Services/ILogService.cs` + `SerilogService.cs` — daily rolling file + console
- `Models/AppConfig.cs`, `Models/CameraConfig.cs`
- Config convention: everything lives in `F:\002_Configs` (`Harry.ini` + `Templates\`), overridable via `HARRY_CONFIG_DIR`

### Phase 2 — JSON loader, DB schema, repository, partitioning
- `Configuration/JsonTemplateModels.cs` + `JsonTemplateLoader.cs` — parse `Result_*.json` / `Settings_*.json` (falls back to local `Templates\`)
- `Infrastructure/DatabaseSchema.cs` — all 9 `CREATE TABLE` statements + per-column metadata for the auto schema-check
- `Infrastructure/MySqlRepository.cs` — connection management, CREATE DATABASE/TABLE, `INFORMATION_SCHEMA` schema-check + `ALTER TABLE ADD COLUMN`
- `Infrastructure/PartitionManager.cs` — provisions current + 3 monthly partitions (splits `p_future`), `DROP PARTITION` retention
- `Services/IDatabaseService.cs` + `MySqlDatabaseService.cs` — startup sequence (connect w/ backoff → schema → partitions → camera sync → definition sync with effective-date history)
- `Models/MeasurementDefinition.cs`, `Models/SettingDefinition.cs`

### Phase 3 — Camera TCP client + telegram parser
- `Communication/TelegramParser.cs` — header (pos 0–3), serial region (4–67), Results/Settings/Diagnostic by signal word; `IsKeepAliveReply()`; extracts measurements/settings via `telegram_place`
- `Communication/TcpCameraClient.cs` — always-client TCP, exponential-backoff reconnect, 8192 buffer, CR framing, Keyence keepalive (`MR,#Version\r`) with ping-response watchdog, typed events
- `Communication/CameraTelegramEventArgs.cs`
- `Models/CameraTelegramTypes.cs`, `ParsedTelegram.cs`, `MeasurementSample.cs`, `SettingSample.cs`
- `Services/ICameraService.cs` + `CameraConnectionService.cs` — one client per camera (dynamic count)

### Phase 4 — Measurement pipeline
- `Models/PendingMeasurement.cs`
- `Services/IMeasurementProcessor.cs` + `MeasurementProcessor.cs` — camera `ResultsReceived` → `ConcurrentQueue` → batched insert into `measurements_serial` / `measurements_serial_trimmer`
- `Services/MeasurementDefinitionCache.cs` — in-memory `(camera, variable) → definition_id`

### Phase 5 — SPS server (7 channels)
- `Communication/TcpSpsServer.cs` — 7 TCP listeners (always server), CR/LF framing
  - Ch1 KeepAlive: mirror + per-camera status string (`1`/`0`, INI order)
  - Ch2 Part Exit: parse → `PartExitReceived` event → ack `OK`
  - Ch3–7 MSA: `Request;<BaseID>` → `Wait`/`OK`/`NG`/`Error` via pluggable `MsaRequestHandler`
- `Models/SpsChannel.cs`, `Models/SpsPartExitData.cs`
- `Services/ISpsServer.cs`

---

## Current State of Each Service / Processor

| Component | State | Runs on | Queue | Notes |
|-----------|-------|---------|-------|-------|
| `MySqlDatabaseService` | ✅ Implemented | Background `Task` (started at app launch) | — | Connect w/ backoff, schema, partitions, syncs; exposes `OpenConnectionAsync` |
| `MySqlRepository` | ✅ Implemented | Called from caller's Task | — | Opens a fresh pooled connection per operation |
| `PartitionManager` | ✅ Implemented | Called from DB service Task | — | Monthly partition create; retention drop method ready (not scheduled yet) |
| `CameraConnectionService` | ✅ Implemented | — | — | Owns N `TcpCameraClient` |
| `TcpCameraClient` (×N) | ✅ Implemented | **One `Task` per camera** | — | Receive loop + keepalive watchdog |
| `MeasurementProcessor` | ✅ Implemented | **Dedicated background `Task`** | **`ConcurrentQueue<PendingMeasurement>`** | Batches into the partitioned tables |
| `MeasurementDefinitionCache` | ✅ Implemented | Loaded once on DB-ready | — | Atomic dictionary swap |
| `TcpSpsServer` | ✅ Implemented | **One `Task` per channel** + one per client | — | Request/response per telegram |
| **`SettingsProcessor`** | ❌ **Not built yet** | — | — | `TcpCameraClient.SettingsReceived` fires but has **no consumer** |
| **`DiagnosticProcessor`** | ❌ **Not built yet** | — | — | `TcpCameraClient.DiagnosticReceived` fires but has **no consumer** |
| Part Exit → `dmcserial` persistence | ❌ **Not built yet** | — | — | `ISpsServer.PartExitReceived` fires but has **no consumer** |
| CSV / Collage / MSA / Image cleanup | ❌ **Not built yet** | — | — | Planned later phases |

---

## Verification Answers

### Q: Are MeasurementProcessor, SettingsProcessor, DiagnosticProcessor all running on separate Tasks with ConcurrentQueue?

**No — only `MeasurementProcessor` exists today.**
- `MeasurementProcessor` ✅ runs on its own dedicated background `Task` and drains a `ConcurrentQueue<PendingMeasurement>` (the camera receive threads only enqueue; all DB I/O happens on the processor Task).
- `SettingsProcessor` ❌ and `DiagnosticProcessor` ❌ are **not yet implemented**. The plumbing is ready — `TcpCameraClient` already raises `SettingsReceived` and `DiagnosticReceived` events — but no queue/processor consumes them yet. They are the next items to build (see below). When built, they will follow the same pattern: dedicated `Task` + `ConcurrentQueue`, matching CLAUDE.md section 14.

### Q: Is MySqlConnection created per-Task, never shared?

**Yes — and even more granular: a connection is opened per operation, never shared across threads/Tasks.**
- Every DB operation calls `OpenConnectionAsync` / `MySqlRepository.OpenAsync`, which does `new MySqlConnection(...)` and is wrapped in `await using` so it is disposed (returned to the pool) when the operation finishes.
- `MeasurementProcessor` opens a fresh connection **per flush** on its single background Task (`MeasurementProcessor.cs:190/220`).
- `MySqlDatabaseService` and `PartitionManager` likewise open per-operation connections.
- No `MySqlConnection` instance is stored in a shared field or reused across Tasks. Connection pooling is enabled in the connection string, so per-operation opens are cheap.
- This satisfies the CLAUDE.md rule "one MySQL connection per thread — no shared connections".

---

## Next Planned Step

**Phase 6 — Settings pipeline + Part Exit persistence** (the two consumers whose events already fire but go nowhere):

1. **`SettingsProcessor`** — dedicated `Task` + `ConcurrentQueue`, consumes `SettingsReceived`, writes limit history to the `settings` table (resolving `setting_definitions.id` via a cache, mirroring `MeasurementProcessor`).
2. **Part Exit → `dmcserial`** — consume `ISpsServer.PartExitReceived`, upsert the finished-part row into `dmcserial`.

Then, following CLAUDE.md:
- **CSV export** (Phase 7) — including the **`DiagnosticProcessor`** (`DiagnosticReceived` → CSV only).
- Image cleanup, Collage, MSA engine, UI controls, companion tools.

---

## Build & Repo

- Builds clean: `dotnet build HarryDataServer.sln -c Release` → 0 warnings, 0 errors (`net8.0-windows`).
- Branch `main`, pushed to `https://github.com/CustomHelp/HarryDataServerV2`.
