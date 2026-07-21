# CLAUDE.md — HarryDataServer V2
## Master Instruction File for Claude Code

> This file is the single source of truth for the entire project.
> Read it completely before writing any code, creating any file, or making any architectural decision.
> Update this file when specifications change.

---

## 1. Project Overview

**HarryDataServer V2** is an industrial data acquisition and quality management system for a 5-blade razor head production line. It runs on a Windows Server embedded in the production machine.

- **Framework:** C# WPF .NET 8.0
- **Database:** MySQL (local, data on drive E:\)
- **Configuration:** INI file (Harry.ini) + JSON template files per camera
- **Language:** All code, comments, variable names, log messages in **English**
- **Architecture:** Multi-threaded, one thread per camera client, ConcurrentQueues for DB writes

---

## 2. Production Line Layout

Two parallel production strands that merge at M50, then packaging:

```
Strand 1:  M10 → M20 → M30/M31/M32 ─┐
                                       ├─→ M50 (St160 Packaging)
Strand 2:  M11 → M21 → M33/M34/M35 ─┘
```

Everything that happens in Strand 1 happens identically in Strand 2.

### Module Descriptions

| Module | Function |
|--------|----------|
| M10 | Lubrastrip glued onto frame. ST30: glue points + frame cosmetics. ST60: lubrastrip position/cleanliness |
| M11 | Identical to M10 (Strand 2) |
| M20 | Trimmer Sub-Assembly preparation. ST60: KF1=top view (2 parts), KF3=side view (2 parts) |
| M21 | Identical to M20 (Strand 2) |
| M30-M35 | Blade assembly modules (no direct camera connection to our system) |
| M50 | Final assembly + full inspection. ST40, ST110(x2), ST120, ST130, ST140 |
| St160 | Packaging station — triggers our Part Exit event |

---

## 3. Camera Controllers (Keyence — we are always TCP Client)

| INI Key | Camera Name | Module | Station | IP | Port |
|---------|-------------|--------|---------|-----|------|
| Camera1 | M10_ST030_KF1 | M10 | ST30 | 172.29.10.30 | 8500 |
| Camera2 | M10_ST060_KF1 | M10 | ST60 | 172.29.10.60 | 8500 |
| Camera3 | M11_ST030_KF1 | M11 | ST30 | 172.29.11.30 | 8500 |
| Camera4 | M11_ST060_KF1 | M11 | ST60 | 172.29.11.60 | 8500 |
| Camera5 | M20_ST060_KF1 | M20 | ST60 | 172.29.20.61 | 8500 |
| Camera6 | M20_ST060_KF3 | M20 | ST60 | 172.29.20.62 | 8500 |
| Camera7 | M21_ST060_KF1 | M21 | ST60 | 172.29.21.61 | 8500 |
| Camera8 | M21_ST060_KF3 | M21 | ST60 | 172.29.21.62 | 8500 |
| Camera9 | M50_ST040_KF1 | M50 | ST40 | 172.29.50.40 | 8500 |
| Camera10 | M50_ST110_KF1 | M50 | ST110 | 172.29.50.111 | 8500 |
| Camera11 | M50_ST110_KF3 | M50 | ST110 | 172.29.50.112 | 8500 |
| Camera12 | M50_ST120_KF1 | M50 | ST120 | 172.29.50.120 | 8500 |
| Camera13 | M50_ST130_KF1 | M50 | ST130 | 172.29.50.130 | 8500 |
| Camera14 | M50_ST140_KF1 | M50 | ST140 | 172.29.50.140 | 8500 |

> All cameras use port 8500.
> Subnet mask: 255.255.0.0 throughout.
> Number of cameras is dynamic — read from INI, never hardcode camera count.

---

## 4. Camera Telegram Protocol

### General Rules
- Delimiter: comma (`,`)
- Decimal separator: dot (`.`)
- End of telegram: carriage return (`\r`)
- TCP buffer: 8192 bytes
- Reconnect strategy: exponential backoff (3s, 6s, 12s, max 60s)
- Keepalive: continuously send version variable request; if no response → camera offline
- **Outage logging (`TcpCameraClient`):** a controller going offline is logged as a **WARNING
  exactly once**, on the `Connected → Disconnected` transition. Subsequent failed reconnect
  attempts for an already-offline controller are logged at **Debug**, and recovery logs one
  **Information** (`reconnected`). This keeps an unreachable camera from inflating the warning
  counter during idle (one Warning per outage, not one per retry).

### NoSerial (bad Results telegram)

A **Results** telegram whose **Serial1** (SZID, the token 3–34 region) is **empty or all `0`
characters** — checked on the already-parsed, 22-char-truncated `ParsedTelegram.Serial1` via
`ParsedTelegram.IsNoSerial` — means the controller produced a **bad telegram** and the data must
not be trusted. Such a telegram is **dropped from the DB pipeline** (`TcpCameraClient.ProcessFrame`
does **not** raise `ResultsReceived`, so neither `MeasurementProcessor` nor `MsaService` writes
anything — no measurement rows, no dmcserial), is logged as a WARNING, and is surfaced as
**`NoSerial`** in the camera control (status text + the "Last telegrams" line). It is still written
to the raw capture file (capture happens before the drop). The check applies to **Results only** —
Settings/Diagnostic have their own paths.

### Raw telegram capture (test/commissioning aid)

A global **"Telegramme mitschneiden"** checkbox (main-window top bar, OFF by default, not
persisted) writes **every incoming real telegram** — exactly as received, before parsing — to
`Capture\Capture_<Controller>_<ddMMyy_HHmmss>.csv` next to the executable (one file per controller,
opened lazily, reused until capture is turned off). Each line is `<ddMMyy_HHmmss>,<raw telegram>`.
**Keepalive lines (`MR,…` / `ER…`) are excluded** — only Results/Settings/Diagnostic telegrams
(incl. NoSerial bad telegrams) are captured. This is intentionally separate from the Diagnostic-CSV
feature (`ITelegramCapture` / `TelegramCaptureService`).

A telegram is one of **three kinds — `Results`, `Settings`, or `Diagnostic`.** Results and
Settings share a header with the signal word at **token 2**. A **Diagnostic** telegram has a
**different layout** (serials first, no version field, the literal word `Diagnostic` at ~token 65)
and is therefore detected by **scanning the tokens for an exact `Diagnostic` token**, not by
position — this check runs *before* the signal-word dispatch (`TelegramParser.ParseLine`).

> **Real layout confirmed (2026-06-29)** from the live Keyence "Datenausgabe" configs
> (M50_ST110, M11_ST030, M50_ST140) + `Result_Header.xlsx`. Every camera outputs the serials as
> **32 separate comma-tokens each** (Keyence "Anzahl 32"); for Results/Settings the signal word is
> at token 2. The earlier assumption of a single operating-mode string at token 3 was wrong and
> dropped every telegram on the live line.

### "Results" Telegram Layout (comma-separated, 0-based token index)

| Token(s) | Content | Notes |
|----------|---------|-------|
| 0 | Controller name | e.g. `M11_ST030_KF1` |
| 1 | Camera program version | e.g. `4.0` |
| 2 | Signal word | always `Results` |
| 3 … 34 | **Serial1** (32 tokens) | concatenated → **padding stripped to meaningful length (≤22)** |
| 35 … 66 | **Serial2** (32 tokens) | concatenated → kept full **32 chars** |
| 67 | `Mode_Diagnostic` (bool 0/1) | **independent** of operating mode — INFO only |
| 68 | `Mode_GoldenSample` (bool 0/1) | → operating mode `LimitSample` |
| 69 | `Mode_MSA1` (bool 0/1) | → operating mode `MSA1` |
| 70 | `Mode_MSA3` (bool 0/1) | → operating mode `MSA3` |
| 71 | `Total_Result` (SINT −2/−1/0/1/2) | camera's overall part result — **display only** |
| 72 … | measurements | alternating `R_` (SINT) / `V_` (Float) pairs |

> **Serial1 (tokens 3–34):** the camera emits Serial1 as 32 single-char tokens, **right-padded with
> `0`** to the field width; the DB serial columns are `VARCHAR(22)` (`Infrastructure/SerialField.cs`,
> `SerialField.MaxLength = 22`). The **SPS part-exit telegram delivers the SAME serial UNPADDED**
> (its true length, **19 chars** on the live line). To make the two compare equal (the part-exit
> measurement lookup joins `measurements_serial(_trimmer)` ↔ `dmcserial` on the serial), **every
> receive path normalises Serial1 through the single `Infrastructure/SerialNumberHelper.Normalize`**
> — it drops the controller's trailing `0` padding down to the meaningful length
> (`[General] SerialNumberLength`, **default 19**) *only when the tail past that length is all `0`*
> (never a blind `TrimEnd('0')`, so a real serial that legitimately ends in `0` is preserved) and
> caps to 22. Applied at `TelegramParser` (camera), `SpsPartExitData.TryParse` (frame + trimmer
> serials) and `MeasurementProcessor`. The **DMC / Serial2 is a separate, wider field and is NOT
> length-normalised**. **Serial2 (tokens 35–66)** keeps its full 32 chars (needed for DMC uniqueness
> in MSA). Image-filename search still uses the 12/14-char prefix, unaffected by the change.
>
> **Operating mode** is derived from the three flags at tokens 68–70: all 0 → `Normal`;
> exactly one set → `MSA1` / `MSA3` / `LimitSample` (GoldenSample → `LimitSample`). Only one is
> ever set; if more than one is set the telegram is logged (WARNING) and treated as `Normal`.
> **`Mode_Diagnostic` (token 67) is independent** — it can be on/off in any mode, is exposed as
> `ParsedTelegram.IsDiagnostic`, and has **no effect on processing or routing** (UI INFO only).
> This boolean flag inside a *Results* telegram is a **completely separate thing** from a
> standalone **Diagnostic telegram** (the signal-word kind, see its own layout below): one is an
> INFO badge on a normal part, the other is a raw diagnostic dump with its own layout.
>
> **`Total_Result` (token 71)** is the camera's overall part result, exposed as
> `ParsedTelegram.OverallResult` for display in the camera control only. The authoritative OK/NG
> decision for collage / CSV / image handling comes from the **PLC at part-exit** (§5 Ch 2), never
> from this camera value.
>
> **Routing (Normal mode):** M1X/M5X carry the SZID in Serial1 → `measurements_serial`;
> M2X (M20/M21) carry the Virtual Serial in Serial1 → `measurements_serial_trimmer`. The module
> is taken from each camera's INI config (`MeasurementProcessor`).
>
> In MSA/LimitSample mode the BaseID lives in **Serial1** (tokens 3–34), immediately followed by
> the 3-digit loop counter (e.g. `10260623083000` + `001`, then padding); the DMC of the test part
> is in **Serial2** (tokens 35–66). In storage the `base_id` column holds only the 14-char BaseID
> and the loop counter goes to `loop_number`.

### "Settings" Telegram Layout

| Token(s) | Content |
|----------|---------|
| 0 | Controller name |
| 1 | Camera program version |
| 2 | Signal word (`Settings`) |
| 3 … | Min/Max pairs per ParameterSet (no serials, no mode flags) |

- Sent at controller startup or when limits change.
- Structure defined per camera in `Settings_CameraName.json` (telegram_place from token 3).
- **Requesting a Settings telegram:** the controller does not send Settings on demand by itself;
  it must be asked by writing a Keyence variable. The Cameras tab has a **"Settings anfordern"**
  button that sends `MW,#Send_Settings,1\r` to **every connected camera** over the existing
  connection (the same socket as the `MR,#Version\r` keepalive — writes are serialized so they
  never interleave). The trailing **CR is mandatory** (without it the controller answers
  `ER,MW,<code>`); on success it replies `MW` (echo, no `OK`). The reply arrives on the receive
  loop and is not inspected — the controller then emits a normal Settings telegram on its next
  trigger, handled by the existing Settings pipeline. (`TcpCameraClient.RequestSettingsAsync`.)

### "Diagnostic" Telegram Layout (different layout — detected by token scan)

A diagnostic telegram does **not** share the Results/Settings header: there is **no version
field**, the serials come **first**, and the word `Diagnostic` sits at **token 65 — not token 2**.
The trailing values are **arbitrary and camera-dependent** (no fixed measurement structure).
Confirmed live example (M50_ST140_KF1):

| Token(s) | Content |
|----------|---------|
| 0 | Controller name (e.g. `M50_ST140_KF1`) |
| 1 … 32 | **Serial1** / SZID (32 tokens) → concatenated, truncated to **22 chars** |
| 33 … 64 | **Serial2** / Trimmer/DMC (32 tokens) → concatenated, kept full **32 chars** |
| 65 | the literal word `Diagnostic` |
| 66 | a label (e.g. `B5 Blade CAM1`) |
| 67 … | arbitrary `VAL_` values (variable count) |

> **Detection:** scan the comma-split tokens for an exact token equal to `Diagnostic`
> (case-insensitive, trimmed). If present, the whole telegram is diagnostic and is routed to the
> diagnostic CSV — normal Results/Settings parsing is skipped. Results/Settings bodies are
> serials/numbers and never contain that word, so there are no false positives.
>
> **Raw CSV dump (`DiagnosticProcessor`):** one row per telegram, plain left-to-right —
> `ReceivedAt` (`DDMMYY_HHMMSS`), Serial1 (≤22), Serial2 (32), then **every remaining token**
> (the `Diagnostic` word, the label and all values) exactly as received. Rows may have different
> column counts (raw dump, not a fixed schema). Output: `Diagnostic_<DDMMYY_HHMMSS>.csv` in
> `[Diagnostic] DiagnosticPath`, rotating to a new file every `[Diagnostic] MaxRows` (default
> 1000). Written to CSV only, never to the DB.
>
> **Not to be confused with the `Mode_Diagnostic` flag** (token 67 of a *Results* telegram, INFO
> only) — that is an independent boolean on a normal part and does not produce a diagnostic dump.

### Result Codes (R_ values)

| Value | Meaning |
|-------|---------|
| -2 | Not Validated |
| -1 | Position Adjustment Error |
| 0 | Result BAD |
| 1 | Result GOOD |
| 2 | Not Evaluated (deactivated) |

### Variable Naming Convention

| Prefix | Type | DB Column | Format |
|--------|------|-----------|--------|
| `R_` | Result | result_status | SINT (5 digits) |
| `V_` | Value | measurement_value | Float (2 decimal) |
| `SET_MIN_` | Setting minimum | min_value | Float (2 decimal) |
| `SET_MAX_` | Setting maximum | max_value | Float (2 decimal) |
| `SET_EVA_` | Evaluation on/off | — | Int (0/1) |
| `CNT_` | Counter | — | Int |

---

## 5. SPS / PLC Connections (we are always TCP Server)

**7 channels, same IP, different ports. All ports configurable in Harry.ini.**

### Channel 1 — KeepAlive / Status
- PLC connects, sends telegram, we mirror it back
- **On success:** mirror + camera status string (one `1`/`0` per camera, semicolon-separated, in INI order)
- **On error:** different response + current error description in plain English
- Example response: `<mirrored_telegram>;1;1;0;1;1;1;0;1;1;1;1;1;1;1`

### Channel 2 — Part Exit (St160 Packaging)
Telegram fields (semicolon-separated) — **15 fields, at least 15 required** (a shorter telegram
is answered with `<32×'0'>;false`). Empty fields are allowed:

| # | Field | Content |
|---|-------|---------|
| 0 | DMC | DataMatrix code lasered on part |
| 1 | SZID | Frame serial number (32 chars) |
| 2 | VirtualSerial | Trimmer serial number (32 chars) |
| 3 | OrderName | Current production order name |
| 4 | Mode | `Normal` / `MSA1` / `MSA3` / `LimitSample` |
| 5 | M1X_Module | Which M1x module (10 or 11) |
| 6 | M1X_Nest | Nest number in M1x |
| 7 | M2X_Module | Which M2x module (20 or 21) |
| 8 | M2X_Nest | Nest number in M2x |
| 9 | M3X_Module | Which M3x blade module |
| 10 | M3X_Nest | Nest number in M3x |
| 11 | M50_Nest | Nest number in M50 |
| 12 | Temperature | Temperature value from M1x (float, dot decimal) → `dmcserial.m1x_temperature` |
| 13 | Humidity | Humidity value from M1x (float, dot decimal) → `dmcserial.m1x_humidity` |
| 14 | ResultStatus | `OK` / `NG` / `DE` (deleted) |

> `M2X_Module` / `M2X_Nest` are parsed as Int; `M3X_*` / `M50_Nest` stay String. Full protocol
> spec for the PLC programmer: `SPS_Schnittstellen.md` §4.

**Triggers after receiving:** CSV export, Collage generation, MSA evaluation (if MSA mode)

### Channels 3–7 — MSA Evaluation Trigger

| Channel | Module |
|---------|--------|
| 3 | M10 |
| 4 | M11 |
| 5 | M20 |
| 6 | M21 |
| 7 | M50 |

**Request telegram:** `Request;<BaseID>` — `<BaseID>` is the bare **14-char** BaseID (no loop
counter). The completion handler aggregates `msa_measurements` on an **exact** `base_id`
match, scoped by `controller_name` (module) for safety.
**Responses:** the requested BaseID is **mirrored back as field 1** so the PLC can correlate
the poll response with its request — format `<Status>;<BaseID>[;<description>]`:
- `Wait;<BaseID>` — currently processing, try again
- `OK;<BaseID>` — MSA passed
- `NG;<BaseID>` — MSA failed
- `Error;<BaseID>;<description>` — error occurred (BaseID field empty when the request
  format was invalid, e.g. `Error;;expected 'Request;<BaseID>'`)

---

## 6. Serial Number Concept

> Serial1 = tokens 3–34, Serial2 = tokens 35–66 (each 32 comma-tokens, see §4).

### Normal Mode
- **M10/M11:** SZID (frame identity) in Serial1 (tokens 3–34). Serial2 empty.
- **M20/M21:** Virtual Serial (trimmer identity) in Serial1 (tokens 3–34). Serial2 empty.
- **M50:** SZID in Serial1 (tokens 3–34). Serial2 empty.
- **Part Exit (Ch2):** All three known: DMC + SZID + VirtualSerial.

### MSA Modes (MSA1, MSA3, LimitSample)
- Serial1 (tokens 3–34): **BaseID (14 chars) + 3-digit loop counter = a fixed 17-char run serial**
  (2 module + 6 date `YYMMDD` + 6 time `HHmmSS` + 3 loop), **right-padded with `0` to the 32-token
  field**. `SerialNumberHelper.Normalize` trims Serial1 to the meaningful length (default 19), which
  still contains the whole 17-char run serial; `BaseId.TrySplitRun` then reads the first 14 chars
  → `base_id` and the next 3 → `loop_number`. The trailing padding is ignored.
- Serial2 (tokens 35–66): **DMC read from the test part — real, up to 32 chars, NEVER trimmed**
  (kept full for DMC uniqueness). Stored verbatim in `msa_measurements.dmc`.

### BaseID Format (14 characters: `10260623083000` = M10, 2026-06-23, 08:30:00)

| Field | Chars | Example |
|-------|-------|---------|
| Module | 2 | `10` |
| Year | 2 | `26` |
| Month | 2 | `06` |
| Day | 2 | `23` |
| Hour | 2 | `08` |
| Minute | 2 | `30` |
| Second | 2 | `00` |

The BaseID (14 chars) stays constant across all stations for one loop of a run. During
the run, **each loop telegram appends a 3-digit loop counter** to the BaseID in the serial
field: loop 1 → `…001` (17 chars total), loop 2 → `…002`, etc. The counter increments each
time the run cycles through again. In storage the **`base_id` column holds only the 14-char
BaseID**, and the loop counter is parsed out into the integer **`loop_number`** column.

When the run completes, the SPS sends the completion signal on the MSA channel as
`Request;<BaseID>` — **the bare 14-char BaseID, with no loop counter** (CLAUDE.md §5).
There is no longer any "MoverNumber" / TrayRow / TrayCol field.

### Image Filename Search
Image filenames start with a 12-character abbreviated serial + underscore.
When searching for images, use only the **first 12 characters** of the SZID.

---

## 7. MSA Calculations

### Pass Criteria Summary

| Test | Pass Condition |
|------|---------------|
| MSA1 Cg | ≥ 1.33 |
| MSA1 Cgk | ≥ 1.33 |
| MSA3 %Tolerance | ≤ 20% |
| LimitSample | 100% of prepared errors must be rejected |

### MSA1 Formulas (50 measurements of 1 part)

```
Tolerance = USL - LSL  (from Settings/Grenzen for that measurement)
σ = StdDev of all 50 measured values

Cg  = (0.20 × Tolerance) / (6 × σ)
Cgk = ((0.20 × Tolerance) - |x̄ - xm|) / (6 × σ)

where:
  x̄  = mean of all 50 measurements
  xm = reference value from MSA JSON reference file
```

### MSA3 Formula (parts × repeated measurements each)

```
Tolerance = USL - LSL

For each part i: x̄i = mean of its measurements
SumSquares = ΣΣ(x̄i - xij)²   (sum over all parts and all measurements)
DegreesOfFreedom = Σ over parts (measurementsPerPart − 1)

%Tolerance = 6 × √(SumSquares / DegreesOfFreedom) / Tolerance
```

> The classic layout is 32 parts × 3 measurements → DoF = 32 × (3 − 1) = 64. **The number
> of parts and loops is controlled entirely by the SPS/PLC and is never hardcoded** — the
> implementation (`MsaCalculator.Msa3`) computes DoF dynamically as
> Σ(measurementsPerPart − 1) over the parts actually present, so it is correct for any
> sample/loop count (e.g. 30 × 3 → DoF = 60). Parts are grouped by DMC; loops are the
> repeated measurements of one part.

### MSA JSON Reference Files
- Location: configurable per module in Harry.ini
- One file per module per MSA version
- Contains: `reference_value` (xm) per measurement name for MSA1
- Contains: expected pass/fail per measurement for LimitSample

### Database Strategy for MSA
- Use **separate table** `msa_measurements` (identical structure to `measurements_serial`)
- Unique business key: DMC + BaseID + loop_number + controller_name
- Primary key: always `BIGINT AUTO_INCREMENT`

---

## 8. Database Schema

### Network
```
Server (this machine):  172.29.1.5   / 255.255.0.0
NAS:                    172.29.1.6   / 255.255.0.0
All cameras:            Port 8500
SPS channels:           Ports 6000–6006
```

### Connection
```
Server:   localhost
Database: camera_data
DataDir:  E:\MySQL\Data\
```

### Database Startup Logic (runs every application start)

```
1. Connect to MySQL (retry with exponential backoff if not available)
2. CREATE DATABASE IF NOT EXISTS camera_data
3. For each table: CREATE TABLE IF NOT EXISTS (full schema)
4. For each table: Schema-Check
   → Query INFORMATION_SCHEMA.COLUMNS
   → Compare with expected columns
   → If column missing: ALTER TABLE ADD COLUMN automatically
   → Log every schema change to Serilog
5. Partition-Check for measurements_serial + measurements_serial_trimmer
   → Check if partitions exist for current month + next 3 months
   → If missing: CREATE partition automatically
6. Camera sync: INSERT or UPDATE cameras table from INI config
7. Definition sync: INSERT or UPDATE measurement_definitions + setting_definitions from JSON files
   → Set effective_end on removed definitions
   → Log all changes
```

Adding a new column only requires a code change — software detects and applies it automatically on next start. No manual SQL, no production stop.

### All tables must be created automatically at application startup if not present.

---

### Table: `cameras`
```sql
CREATE TABLE IF NOT EXISTS cameras (
  id           INT AUTO_INCREMENT PRIMARY KEY,
  camera_name  VARCHAR(100) NOT NULL,
  module       VARCHAR(10)  NOT NULL,
  ip_address   VARCHAR(15)  NOT NULL,
  port         INT          NOT NULL,
  active       TINYINT      NOT NULL DEFAULT 1,
  created_at   DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uk_camera_name (camera_name)
);
```

### Table: `measurement_definitions`
```sql
CREATE TABLE IF NOT EXISTS measurement_definitions (
  id              INT AUTO_INCREMENT PRIMARY KEY,
  camera_id       INT          NOT NULL,
  telegram_place  INT          NOT NULL,
  variable_name   VARCHAR(100) NOT NULL,
  display_name    VARCHAR(100) NOT NULL,
  var_type        VARCHAR(10)  NOT NULL,  -- 'Result' or 'Value'
  parameter_set   INT          NOT NULL,
  module_ref      VARCHAR(10)  NOT NULL DEFAULT 'NoRef',
  feature_group   VARCHAR(100) NOT NULL DEFAULT 'NoGroup',
  effective_from  DATE         NOT NULL,
  effective_end   DATE,
  FOREIGN KEY (camera_id) REFERENCES cameras(id)
);
```

### Table: `setting_definitions`
```sql
CREATE TABLE IF NOT EXISTS setting_definitions (
  id             INT AUTO_INCREMENT PRIMARY KEY,
  camera_id      INT          NOT NULL,
  telegram_place INT          NOT NULL,
  setting_name   VARCHAR(100) NOT NULL,
  parameter_set  INT          NOT NULL,
  limit_type     VARCHAR(5)   NOT NULL,  -- 'Min' or 'Max'
  FOREIGN KEY (camera_id) REFERENCES cameras(id)
);
```

### Table: `settings` (limit history)
```sql
CREATE TABLE IF NOT EXISTS settings (
  id             INT AUTO_INCREMENT PRIMARY KEY,
  camera_id      INT      NOT NULL,
  definition_id  INT      NOT NULL,
  parameter_set  INT      NOT NULL,
  limit_value    DOUBLE   NOT NULL,
  version        VARCHAR(20),
  recorded_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX idx_camera_recorded (camera_id, recorded_at),
  FOREIGN KEY (camera_id) REFERENCES cameras(id),
  FOREIGN KEY (definition_id) REFERENCES setting_definitions(id)
);
```

### Table: `dmcserial` (one row per finished part)
```sql
CREATE TABLE IF NOT EXISTS dmcserial (
  id              INT AUTO_INCREMENT PRIMARY KEY,
  serial_number   VARCHAR(50)   NOT NULL,
  serial_trimmer  VARCHAR(50),
  dmc             VARCHAR(50),
  m1x_module      TINYINT,
  m1x_nest        INT,
  m2x_module      TINYINT,
  m2x_nest        INT,
  m3x_module      VARCHAR(10),
  m3x_nest        VARCHAR(10),
  m50_nest        VARCHAR(10),
  order_name      VARCHAR(100),
  m1x_temperature DOUBLE,
  m1x_humidity    DOUBLE,
  result_status   TINYINT      NOT NULL DEFAULT 0,  -- 1=OK, 0=NG, -1=deleted
  created_at      DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uk_serial (serial_number),
  INDEX idx_dmc (dmc),
  INDEX idx_trimmer (serial_trimmer),
  INDEX idx_order (order_name),
  INDEX idx_created (created_at)
);
```

### Table: `measurements_serial` (PARTITIONED by day)
```sql
CREATE TABLE IF NOT EXISTS measurements_serial (
  id                 BIGINT   NOT NULL AUTO_INCREMENT,
  serial_number      VARCHAR(50) NOT NULL,
  definition_id      INT      NOT NULL,
  measurement_value  DOUBLE,
  measurement_string VARCHAR(20),
  result_status      TINYINT,
  run_type           TINYINT  NOT NULL DEFAULT 0,  -- 0=Normal,1=MSA1,2=MSA3,3=LimitSample,4=GoldenSample
  measured_at        DATETIME NOT NULL,
  PRIMARY KEY (id, measured_at),
  INDEX idx_serial (serial_number),
  INDEX idx_def (definition_id),
  INDEX idx_measured (measured_at)
) PARTITION BY RANGE (TO_DAYS(measured_at)) (
  PARTITION p_2026_06 VALUES LESS THAN (TO_DAYS('2026-07-01')),
  PARTITION p_2026_07 VALUES LESS THAN (TO_DAYS('2026-08-01')),
  PARTITION p_2026_08 VALUES LESS THAN (TO_DAYS('2026-09-01')),
  PARTITION p_2026_09 VALUES LESS THAN (TO_DAYS('2026-10-01')),
  PARTITION p_2026_10 VALUES LESS THAN (TO_DAYS('2026-11-01')),
  PARTITION p_2026_11 VALUES LESS THAN (TO_DAYS('2026-12-01')),
  PARTITION p_2026_12 VALUES LESS THAN (TO_DAYS('2027-01-01')),
  PARTITION p_future  VALUES LESS THAN MAXVALUE
);
```

### Table: `measurements_serial_trimmer` (PARTITIONED, same structure)
Same as `measurements_serial` but uses `serial_trimmer` instead of `serial_number`.
For M20/M21 measurements only.

### Table: `msa_measurements` (MSA runs — separate from production)
```sql
CREATE TABLE IF NOT EXISTS msa_measurements (
  id                 BIGINT      NOT NULL AUTO_INCREMENT,
  dmc                VARCHAR(50) NOT NULL,
  base_id            VARCHAR(50) NOT NULL,  -- 14-char BaseID (MMYYMMDDHHmmSS); never includes the loop counter
  loop_number        INT         NOT NULL,  -- 3-digit per-loop counter parsed from the run serial field
  controller_name    VARCHAR(100) NOT NULL,
  definition_id      INT         NOT NULL,
  measurement_value  DOUBLE,
  measurement_string VARCHAR(20),
  result_status      TINYINT,
  msa_type           VARCHAR(20) NOT NULL,  -- 'MSA1', 'MSA3', 'LimitSample'
  msa_version        VARCHAR(50),
  measured_at        DATETIME    NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  INDEX idx_dmc_baseid (dmc, base_id),
  INDEX idx_baseid_controller (base_id, controller_name),  -- exact-match completion lookup
  INDEX idx_controller (controller_name),
  INDEX idx_measured (measured_at)
);
```

> **MSA vs production storage parity (verified 2026-07-20).** `msa_measurements` stores the *same*
> measurement data as `measurements_serial`, only into a different table with extra key columns.
> Verified end-to-end:
>
> - **Extraction is genuinely shared, not a copy.** `TcpCameraClient.ProcessFrame` calls
>   `TelegramParser.ExtractMeasurements` **once** and raises `ResultsReceived` with that single
>   `IReadOnlyList<MeasurementSample>` instance. `MeasurementProcessor` (Normal) and `MsaService`
>   (MSA) are both subscribers to the *same event* and receive the *same instance*. Both then call
>   the same static `MeasurementRowBuilder.Build` for the R_/V_ pairing. So there is no second
>   extraction that could drift.
> - **Common columns — identical source & type:**
>
>   | Column | Type (both tables) | Production source | MSA source |
>   |--------|--------------------|-------------------|------------|
>   | `definition_id` | `INT NOT NULL` | cache lookup on (camera, R_ variable) | **same** cache lookup on (controller, R_ variable) |
>   | `measurement_value` | `DOUBLE NULL` | V_ partner float (`row.Value`) | **same** (`row.Value`) |
>   | `measurement_string` | `VARCHAR(20) NULL` | V_ `RawField` when unparseable | **same** |
>   | `result_status` | `TINYINT NULL` | R_ status (`row.ResultStatus`) | **same** |
>   | `measured_at` | `DATETIME NOT NULL`¹ | `DateTime.Now` in the receive handler | **same** |
>
>   ¹ `msa_measurements.measured_at` additionally has `DEFAULT CURRENT_TIMESTAMP`, but the code
>   always supplies the value, so the stored value is identical. No type/rounding/NULL difference on
>   any common column.
> - **MSA-only columns:** `dmc` ← Serial2 (verbatim, ≤32); `base_id` ← first 14 chars of Serial1
>   (`BaseId.TrySplitRun`); `loop_number` ← next 3 chars of Serial1 (int); `controller_name` ←
>   telegram token 0; `msa_type` ← operating-mode flags (tokens 68–70); `msa_version` ← telegram
>   token 1 (null if empty).
> - **Separate queue/flush, same interval.** Each processor has its **own** `ConcurrentQueue` +
>   flush loop; both use `[SQLSettings] SaveIntervalSeconds`. They are therefore **not synchronised**
>   — MSA rows are committed on MsaService's own tick (up to `SaveIntervalSeconds` after receipt), so
>   a completion `Request` arriving immediately after a run can see 0 rows (handled by the idempotent
>   `Wait`, §5). Production batches multi-row `INSERT`s of `[SQLSettings] BatchSize`; MSA inserts
>   row-by-row inside one transaction — a performance difference only, not a data difference.
> - **Minor, non-data differences:** an unresolved `definition_id` is dropped in both paths, but the
>   production path logs it once (`WarnUnknown`) while the MSA path drops it silently. The INSERT SQL
>   is duplicated across `MeasurementProcessor.InsertChunkAsync` and `MsaService.InsertOneAsync`
>   (they agree today but are separate code that could drift). `MsaService` also passes the DMC into
>   `MeasurementRowBuilder.Build`'s unused `serial` slot — harmless (MSA never reads `row.Serial`).
>
> **Conclusion: no mapping deviation → no code change.** The Problem-2 symptom (value 0/1,
> `result_status` NULL) is upstream telegram content (pass/fail arriving in the V_ field with an
> empty R_ field), surfaced by `MsaService.LogMsaExtractionDiagnostics`, not a storage defect.

### Table: `msa_results` (computed MSA evaluation results)
```sql
CREATE TABLE IF NOT EXISTS msa_results (
  id              INT AUTO_INCREMENT PRIMARY KEY,
  controller_name VARCHAR(100) NOT NULL,
  dmc             VARCHAR(50)  NOT NULL,
  base_id         VARCHAR(50)  NOT NULL,
  msa_type        VARCHAR(20)  NOT NULL,
  msa_version     VARCHAR(50),
  definition_id   INT          NOT NULL,
  display_name    VARCHAR(100),
  cg_value        DOUBLE,       -- MSA1 only
  cgk_value       DOUBLE,       -- MSA1 only
  pct_tolerance   DOUBLE,       -- MSA3 only
  passed          TINYINT      NOT NULL,
  evaluated_at    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  INDEX idx_dmc (dmc),
  INDEX idx_controller_type (controller_name, msa_type)
);
```

### Partition Management
- **Retention:** Drop partitions older than configured days using `ALTER TABLE DROP PARTITION`
- **New partitions:** Create monthly partitions automatically (background task, runs at startup and monthly)
- **Never use DELETE for retention** on partitioned tables — always DROP PARTITION

### Database Users
```
SettData / 1234Set  → full DDL + DML, localhost only (main application)
GetData  / 1234Get  → SELECT only, network access (Power BI, analysis tools)
```
> Customer will change passwords after deployment.

### Post-Install SQL (run once after MySQL installation)
```sql
-- Fix SettData to localhost only
DROP USER 'SettData'@'%';
CREATE USER 'SettData'@'localhost' IDENTIFIED BY '1234Set';
GRANT ALL PRIVILEGES ON camera_data.* TO 'SettData'@'localhost';

-- Restrict GetData to SELECT only
GRANT SELECT ON camera_data.* TO 'GetData'@'%';

FLUSH PRIVILEGES;
```

### MySQL Performance Settings (my.ini on E:\MySQL\Data\)
Add these to [mysqld] section after installation:
```ini
# Memory - server has 64GB RAM
innodb_buffer_pool_size         = 32G
innodb_buffer_pool_instances    = 8
innodb_log_file_size            = 1G
innodb_flush_log_at_trx_commit  = 2
innodb_flush_method             = O_DIRECT

# Connections
max_connections                 = 200
thread_cache_size               = 16

# Performance
innodb_io_capacity              = 2000
innodb_io_capacity_max          = 4000
innodb_read_io_threads          = 8
innodb_write_io_threads         = 8

# Partitioning
innodb_file_per_table           = 1
```

---

## 9. JSON Template File Format

Location: configurable per camera in Harry.ini (`JsonParameters=` and `JsonSettings=`)

### Result JSON (`Result_CameraName.json`)

> **`telegram_place` starts at 72, NOT 71.** Token 71 is `Total_Result` (§4, display only); the
> first R_/V_ measurement pair is at tokens 72/73. A template that numbers the first R_ at 71 is
> off by one — every R_ then reads `Total_Result`/a V float and every V_ reads an R status, so the
> DB stores the status in `measurement_value` and NULL in `result_status`. (This exact off-by-one
> in the live M2X/M5X templates broke both production and MSA until 2026-07-21.)

```json
{
  "camera": "M50_ST110_KF1",
  "signal_word": "Results",
  "measurements": [
    {
      "telegram_place": 72,
      "variable_name": "R_Anode_Flatness_L",
      "display_name": "Anode_Flatness_L",
      "type": "Result",
      "format": "SINT",
      "parameter_set": 1,
      "module_ref": "NoRef",
      "feature_group": "Anode Measured"
    },
    {
      "telegram_place": 73,
      "variable_name": "V_Anode_Flatness_L",
      "display_name": "Anode_Flatness_L",
      "type": "Value",
      "format": "Float",
      "parameter_set": 1,
      "module_ref": "NoRef",
      "feature_group": "Anode Measured"
    }
  ]
}
```

### Settings JSON (`Settings_CameraName.json`)
```json
{
  "camera": "M50_ST110_KF1",
  "signal_word": "Settings",
  "settings": [
    {
      "telegram_place": 3,
      "setting_name": "Anode_Height_Min",
      "parameter_set": 1,
      "limit_type": "Min",
      "format": "Float"
    }
  ]
}
```

### JSON Loader Behavior at Startup
1. Load all JSON files specified in INI
2. For each camera: INSERT or UPDATE `measurement_definitions` and `setting_definitions`
3. Use `effective_from` / `effective_end` for historical tracking
4. Log any changes to definition names or telegram places

---

## 10. INI Configuration (Harry.ini)

> **Config location:** All configuration lives in the central folder `F:\002_Configs`
> (Harry.ini, the `Templates\` subfolder with the JSON files, and later Collage.ini /
> MSA references). The application looks for Harry.ini in this order:
> `HARRY_CONFIG_DIR` env var → `F:\002_Configs` → next to the executable →
> legacy `D:\HarryDataServer`.
> **Relative paths** in Harry.ini (e.g. `Templates\Result_*.json`) are resolved
> against the directory that contains Harry.ini, so the whole config folder is portable.

```ini
[General]
LogFilePath=D:\HarryDataServer\Logs\
LoggingActive=true
Language=English

[MySQL]
Server=localhost
Database=camera_data
User=SettData
Password=1234Set
RetentionPeriodDays=35

[CSV]
CSV_BasePath=Y:\02_CSV_Merge      ; production CSV → CSV_BasePath\YYYY\MM\DD
CSV_DiagnosticPath=Y:\03_CSV_Diagnostic  ; diagnostic CSV → \YYYY\MM\DD
DataSetsPerFile=10000
CSV_Save=true
CSVMSA_Save=true
CSVDiagnostic_Save=true

[NAS]
NAS_BasePath=Z:\Images
LowResIndividualPath=Z:\Images\01_Low_Resolution_Individual\Input
CollagePath=Z:\Images\02_Collage\Input
HighResNGPath=Z:\Images\03_High_Resolution_NG\Input
HighResDiagnosticPath=Z:\Images\04_High_Resolution_Diagnostic\Input
HighResGoldenSamplePath=Z:\Images\05_High_Resolution_GoldenSample\Input
FullResRetentionDays=30   ; default full-res retention (SOW §5.2.3); per-type keys fall back to it
RetentionNGDays=30
RetentionDiagnosticDays=30
RetentionGoldenSampleDays=30
RetentionCollageDays=30   ; finished-collage retention; falls back to FullResRetentionDays
DeleteAfterCollage=true

[Collage]
Collage_IniPath=D:\HarryDataServer\Collage.ini
Collage_Generate=true
MaxFileSizeKB=128         ; max collage output size in KB (SOW §5.2.2)

[MSA]
ReferencePath=MSA_References   ; per-module MSA_<module>.json INPUT definitions (persistent)
ResultPath=Y:\01_MSA_Results   ; per-run OUTPUT root → ResultPath\YYYY\MM\DD\<BaseID>\{PDF,CSV,IMG}

[SPS]
IP=172.29.1.5
PortKeepAlive=6000
PortPartExit=6001
PortMSA_M10=6002
PortMSA_M11=6003
PortMSA_M20=6004
PortMSA_M21=6005
PortMSA_M50=6006
AutoConnect=true

[SQLSettings]
BatchSize=100
SaveIntervalSeconds=1

[Camera1]
CameraName=M10_ST030_KF1
IP=172.28.10.30
Port=8001
JsonParameters=Templates\Result_M10_ST030_KF1.json
JsonSettings=Templates\Settings_M10_ST030_KF1.json
AutoConnect=true

; ... Camera2 through Camera14 follow same pattern
```

---

## 11. Image Management

### NAS Folder Structure
```
Z:\Images\
  01_Low_Resolution_Individual\Input\   → OK images (source for collage)
  02_Collage\Input\                     → finished collages
  03_High_Resolution_NG\Input\          → NG images
  04_High_Resolution_Diagnostic\Input\  → Diagnostic images
  05_High_Resolution_GoldenSample\Input\ → GoldenSample images
```
NAS auto-sorts images into date subfolders (YYYY\MM\DD).

### Image Filename Format

The Keyence controller writes the filename. The **same** camera program runs in Normal and MSA
mode, so the structure is identical — only the first two fields differ. The field separator is
the **hyphen `-`** (the SZID contains an underscore after char 12, so `_` cannot be the separator):

```
<Field1 22>-<Field2 32>-<overall 1|0>-<Controller>-<Nest>-&<ImageName>.png
```

| Field | Normal mode | MSA / LimitSample mode |
|-------|-------------|------------------------|
| Field 1 (≤22 chars) | SZID (frame serial) | BaseID(14) + Loop(3) + padding |
| Field 2 (32 chars) | 32 zeros (ignore) | DMC printed on the test part |
| overall `1\|0` | overall camera result (1=OK, 0=NG) | same |
| Controller | camera controller name (e.g. `M50_ST040_KF1`) | same |
| Nest | nest number under the camera | same |
| `&<ImageName>` | image identifier (a camera may save several images per part, e.g. height/bright-light) — may contain dots | same |

> Parser: `Infrastructure/ImageFileName.cs`. Field 1 is everything up to the first `-` (it never
> contains a `-`), capped to **22 chars** (`SerialField.MaxLength`) to match the stored Serial1.
> Field 2 (DMC) **may** contain hyphens, so it is recovered by anchoring on the `&ImageName` tail
> rather than a naive split.
> **Search keys:** Normal = first 12 chars of Field 1 (SZID, NG cleanup linkage); MSA = first 14
> chars of Field 1 (BaseID, run-image collection — matches all loops regardless of the 22-char
> Serial1). The 22-char Serial1 is the stored/DB form; image matching uses the shorter prefixes.

### MSA Result Collection (on run complete)

When the SPS sends `Request;<14-char BaseID>` (run finished), the server gathers everything for
that run under `[MSA] ResultPath` (date folder from the **BaseID timestamp**, not now):

```
<ResultPath>\YYYY\MM\DD\<BaseID>\
      ├── PDF\   (AllResults + FailuresOnly PDF reports)
      ├── CSV\   (the MSA measurement CSV)
      └── IMG\   (all run images, MOVED from the GoldenSample input folder)
```

Run images are those whose Field 1 starts with the 14-char BaseID (loop + padding follow).
`[MSA] ResultPath` (per-run OUTPUT) is kept **separate** from `[MSA] ReferencePath` (the persistent
`MSA_<module>.json` INPUT definitions written by HarryLimitSample). Helper: `Infrastructure/MsaResultLayout.cs`.

### Delete Logic
- **NG / Diagnostic / GoldenSample images + finished collages:** the NAS sorts them out of each
  `\Input` folder into sibling `YYYY\MM\DD` day-folders. `ImageCleanupService` walks those
  day-folders and deletes a **whole day-folder** once its date (from the **folder name**, not the
  file timestamp) is older than retention. Per-type retention keys: `RetentionNGDays`,
  `RetentionDiagnosticDays`, `RetentionGoldenSampleDays`, `RetentionCollageDays` (each falls back
  to `FullResRetentionDays`).
- **NG low-res linkage:** when an NG day-folder is deleted, the matching low-res individual images
  (linked by the 12-char serial prefix) are deleted with it.
- **OK images:** deleted after the collage is created (if configured).
- **Image search key:** use only the first 12 characters of the SZID (Field 1).

---

## 12. Collage Generator

- Triggered on Part Exit when result = OK
- Search for images by first 12 chars of SZID and VirtualSerial
- Layout defined by `Collage.ini` (created by separate CollageCreator tool)
- Output to NAS `02_Collage\Input\`
- Run as background task — must not block main thread

### Collage.ini structure (unchanged from V1)
```ini
[CollageSettings]
CanvasWidth=320
CanvasHeight=650
BackgroundColor=White

[Image1]
TemplateName=<serial_pattern>_M50_ST120_KF1_1_&Cam1Img.bmp
Pos_X=160
Pos_Y=339
Scale=1
Zoom=1.1
Crop_X=16
Crop_Y=42
Crop_Width=282
Crop_Height=147
Mirror_X=false
Mirror_Y=false
KeyName=M50_ST120_KF1
```

---

## 13. CSV Export

### Main CSV (triggered on Part Exit)
- One row per finished part containing ALL measurement values from ALL cameras
- Header: dynamic from `measurement_definitions` table
- Missing values (camera was offline): empty column
- File rotation: on order name change OR when `DataSetsPerFile` rows reached
- Filename: `YYYY-MM-DD-HH-mm-OrderName.csv`
- Path: `CSV_BasePath\YYYY\MM\DD\`

### MSA/Evaluation CSV
- Exported on MSA evaluation completion into the per-run folder
  `[MSA] ResultPath\YYYY\MM\DD\<BaseID>\CSV\` (not a global CSV path)
- Contains: Cg, Cgk (MSA1) or %Tolerance (MSA3) per measurement
- Failed measurements highlighted in export (add column `Passed` 0/1)

### Diagnostic CSV
- Written immediately on each Diagnostic telegram (no waiting for Part Exit)
- File rotation: on `DataSetsPerFile` rows

---

## 14. Application Architecture

### Solution Structure
```
HarryDataServer.sln
└── HarryDataServer/                    (WPF .NET 8.0)
    ├── App.xaml / App.xaml.cs          (DI container setup)
    ├── MainWindow.xaml/.cs             (tab layout)
    ├── Views/                          (additional windows)
    ├── Controls/
    │   ├── ucCameraControl.xaml/.cs    (one per camera, dynamic)
    │   ├── ucSpsControl.xaml/.cs       (7-channel SPS server)
    │   ├── ucDatabaseControl.xaml/.cs  (DB status + stats)
    │   ├── ucCsvControl.xaml/.cs       (CSV export status)
    │   ├── ucCollageControl.xaml/.cs   (collage generator)
    │   └── ucMsaControl.xaml/.cs       (MSA tab per module)
    ├── Services/
    │   ├── IConfigService.cs + IniConfigService.cs
    │   ├── IDatabaseService.cs + MySqlDatabaseService.cs
    │   ├── ICsvService.cs + CsvService.cs
    │   ├── ICollageService.cs + CollageService.cs
    │   ├── IMsaService.cs + MsaService.cs
    │   ├── IImageCleanupService.cs + ImageCleanupService.cs
    │   └── ILogService.cs + SerilogService.cs
    ├── Communication/
    │   ├── TcpCameraClient.cs          (one instance per camera)
    │   ├── TcpSpsServer.cs             (7 listeners)
    │   └── TelegramParser.cs           (parses all 3 telegram types)
    ├── Models/
    │   ├── CameraConfig.cs
    │   ├── MeasurementDefinition.cs
    │   ├── Measurement.cs
    │   ├── Setting.cs
    │   ├── SpsPartExitData.cs
    │   ├── MsaRunData.cs
    │   └── BaseId.cs
    ├── Configuration/
    │   ├── IniConfigManager.cs
    │   └── JsonTemplateLoader.cs
    ├── Infrastructure/
    │   ├── MySqlRepository.cs
    │   ├── PartitionManager.cs
    │   └── CsvWriter.cs
    └── Resources/
        └── Templates/                  (JSON files)
```

### Threading Model

| Thread/Task | Responsibility | Priority |
|-------------|---------------|----------|
| UI Thread (STA) | WPF rendering | Normal |
| TcpCameraClient × N | One per camera, receive + enqueue | AboveNormal |
| MeasurementProcessor | ConcurrentQueue → DB | Normal |
| SettingsProcessor | ConcurrentQueue → DB | BelowNormal |
| DiagnosticProcessor | ConcurrentQueue → CSV | BelowNormal |
| TcpSpsServer × 7 | SPS channel listeners | AboveNormal |
| PartExitProcessor | CSV + Collage + MSA trigger | Normal |
| MsaCalculator × 5 | Per-module MSA evaluation | BelowNormal |
| RetentionJob | DB partition drop + image delete | Lowest |
| PartitionManager | Create future monthly partitions | Lowest |

### Key Rules
- **One MySQL connection per thread** — no shared connections
- **ConcurrentQueue<T>** for all inter-thread data passing
- **Never block camera receive thread** with DB or file I/O
- **Dispatcher.Invoke** only for UI updates from background threads
- **isProcessing flag** per processor to prevent duplicate processing tasks

### Dependency Injection
All services registered in `App.xaml.cs` as Singleton:
```csharp
services.AddSingleton<IConfigService, IniConfigService>();
services.AddSingleton<IDatabaseService, MySqlDatabaseService>();
services.AddSingleton<ICsvService, CsvService>();
services.AddSingleton<ICollageService, CollageService>();
services.AddSingleton<IMsaService, MsaService>();
services.AddSingleton<IImageCleanupService, ImageCleanupService>();
services.AddSingleton<ILogService, SerilogService>();
services.AddTransient<TcpCameraClient>();
```

### Theming (suite-wide light/dark)
All apps support a runtime **light/dark** switch via a `ThemeManager` static
(`HarryDataServer/Theming/ThemeManager.cs` for the server, `HarryShared/Theming/ThemeManager.cs`
for the companion tools — same logic). It mutates the palette `SolidColorBrush` instances in the
application resources **in place**, so every `DynamicResource` consumer (views + implicit styles in
`Themes/DarkTheme.xaml`) updates live without reloading any window. `Accent`/`AccentLight` and the
semantic LED colours stay constant across both themes. The choice is persisted to
`%LOCALAPPDATA%\HarrySuite\theme.txt` and is therefore **shared across the whole suite**. Each
window calls `ThemeManager.Initialize()` at startup and exposes a toggle button (top bar on the
server; per-tool on the companions). Default is Dark when nothing is saved.

### App-level UI behaviours (server)
- **Single instance:** the server is single-instance (named `Mutex`). A second launch signals the
  running instance (named `EventWaitHandle`) to bring its window to the foreground, then exits — it
  never binds the TCP ports / DB twice. A crashed primary leaves no stale lock (the kernel mutex is
  released on process death; only `createdNew` is read, never `WaitOne`). (`App.xaml.cs`)
- **Tools tab:** lists the companion apps (HarryAnalysis, HarryGraph, HarryCounter, HarryLimitSample,
  HarryCollageCreator) and launches them with `Process.Start`. Each exe is discovered **next to the
  running exe** (`<name>.exe` or a `<name>\<name>.exe` sibling) — no hardcoded paths; a missing exe
  shows a disabled button with a "not found next to exe" hint. (`CompanionToolViewModel`)
- **Log tab autoscroll:** console/chat style — follows the newest entry (now at the **bottom**) only
  while the view is scrolled to the bottom; scrolling up holds position across the per-tick rebuild;
  returning to the bottom resumes following. (`ucLogControl.xaml.cs`)
- **Copy serial:** right-clicking a line in a camera tile's "Last telegrams" list offers
  *"Seriennummer kopieren"*, copying just the 22-char Serial1 to the clipboard. (`ucCameraControl`)

---

## 15. NuGet Packages

| Package | Version | Purpose |
|---------|---------|---------|
| MySqlConnector | 2.x | MySQL (async-capable, replaces MySql.Data) |
| Serilog | 3.x | Structured logging |
| Serilog.Sinks.File | 5.x | Log to file |
| Microsoft.Extensions.DependencyInjection | 8.x | DI container |
| Microsoft.Extensions.Hosting | 8.x | Host builder |
| IniParser | 3.x | INI file reading |
| System.Text.Json | built-in | JSON template files |
| OxyPlot.Wpf | 2.x | Charts in MSA view and graph tool |
| CommunityToolkit.Mvvm | 8.x | MVVM helpers |
| QuestPDF | 2026.x | MSA PDF report generation (Community licence; SOW §3.2.1) |

**Do NOT use:**
- `MySql.Data` (use MySqlConnector instead)
- `ClosedXML` or `DocumentFormat.OpenXml` (Excel replaced by JSON)

---

## 16. Companion Tools (separate WPF applications, same solution)

### HarryAnalysis — Scanner Tool
- Operator scans DMC barcode
- Fetch all data for that part from DB
- Display: measurements + names + limits + general info (humidity etc.) + results
- Export to CSV
- DB user: `GetData` (read-only)
- **Resolves measurements even without a `dmcserial` part record.** `FindPartForInspectionAsync`
  first tries the `dmcserial` header (rich part info); if there is no part-exit row yet, it
  synthesizes a part directly from `measurements_serial`/`measurements_serial_trimmer` matched by
  serial, so camera-only data (before the PLC part-exit) is still inspectable. Matching is an exact
  serial `=` (no length/32-char assumption), so 22-char Serial1 values match.

### HarryGraph — Measurement Graph
- Select one or more measurements from DB definition list
- Display as time-series chart (OxyPlot)
- Modes: Live (auto-refresh) or fixed time range
- Zoom/pan in chart, print option
- Save/load graph configurations as JSON
- **Range search is date+time** (from/to each a date picker + an `HH:mm:ss` time box, filtered on
  `measured_at`), so production-rate data can be narrowed. **Live view** shows the **last N points
  per series** via an editable combo (presets 10/100/1000/10000 + custom), applied as a SQL
  `LIMIT N` (`LiveView` in `HarryShared`).
- **Picker lists each measurement once (Result definitions only).** Each R_/V_ pair is stored as one
  `measurements_serial` row keyed by the **Result** definition that carries *both* `result_status`
  and the float `measurement_value` (`MeasurementRowBuilder`); the Value definitions have no rows of
  their own. So HarryGraph loads `GetActiveDefinitionsAsync("Result")` — each trend appears once
  (count ≈ halved) and the series plots the float value via the existing `measurement_value` query
  (no query rewrite). The label is the shared `display_name`, so "Result" is never visible.

### HarryMSA — MSA Analysis Tool (or integrate as tab in main app)
- Per-module view of MSA runs
- Table of Cg/Cgk/%Tolerance per measurement
- Red highlight on failed measurements
- Show what caused CGK to fail
- Export to CSV

### HarryCounter — Error Counter (port from RazorErrorCount)
- Count NG parts by time period
- Group by error category (from JSON: FeatureGroup)
- Group by nest number
- Live view + historical
- The TreeView **preserves the user's expand/collapse + selection across (live) refreshes** by a
  stable path key; new nodes appear collapsed. A **"Reset Tree"** button collapses back to the
  default state (top level expanded). First build / grouping change / Reset apply the default.
- **Range search is date+time** (from/to date picker + `HH:mm:ss` box, filtered on
  `measured_at`/`created_at`). **Live view** aggregates over the **last N finished parts** (the most
  recent N `dmcserial` rows by `created_at`, `LIMIT N` subquery) via the same editable combo
  (10/100/1000/10000 + custom). Live ignores the date range; non-live uses it.

### HarryCollageCreator — Collage Layout Editor
- Visual editor for Collage.ini
- Place, zoom, crop, mirror images on canvas
- Save as Collage.ini

### HarryLimitSample — LimitSample Editor
- Scan a part DMC → load all its measurements from DB
- Mark each measurement as "should pass" / "should fail" / "ignore"
- Save as LimitSample JSON reference file
- Manage (add/delete) entries
- Uses `FindPartForInspectionAsync` (like HarryAnalysis), so a part is found even without a
  `dmcserial` record (direct measurements resolution by serial).

---

## 17. Build & Deploy

### Build
```
dotnet build HarryDataServer.sln --configuration Release
```

### Deploy
1. Copy `Release` output to server
2. Place config in the central folder `F:\002_Configs`: `Harry.ini`, `Collage.ini`,
   and the `Templates\` subfolder with the JSON files (template paths are relative).
3. (Optional) Set `HARRY_CONFIG_DIR` to override the config folder location.
4. Application creates DB and all tables on first startup
5. Customer changes DB passwords after successful test

### Git Repository
`https://github.com/CustomHelp/HarryDataServerV2`

Commit message convention:
```
feat: add TcpCameraClient with reconnect logic
fix: handle empty VirtualSerial in M50 telegram
db: add msa_results table
config: update Harry.ini template
```

---

## 18. Implementation Order

Build in this sequence — each phase must compile before starting the next:

1. **Solution skeleton** — project files, DI setup, IniConfigManager, SerilogService
2. **JSON Loader + DB Schema** — JsonTemplateLoader, MySqlRepository, all CREATE TABLE
3. **Camera TCP Client** — TcpCameraClient, TelegramParser (one camera, test with M50_ST110_KF1)
4. **Measurement pipeline** — ConcurrentQueue → MeasurementProcessor → DB insert
5. **Settings pipeline** — SettingsProcessor → DB insert
6. **SPS Server** — TcpSpsServer, all 7 channels, KeepAlive + PartExit
7. **CSV Export** — CsvService, all 3 types, file rotation
8. **Image Cleanup** — ImageCleanupService, retention jobs
9. **Collage** — CollageService, Collage.ini reader
10. **MSA Engine** — MsaService, Cg/Cgk/%Tolerance calculations
11. **UI — Main Window + UserControls** — all ucXxx controls, MVVM bindings
12. **MSA UI** — ucMsaControl, results display, CSV export
13. **Integration** — all cameras running parallel, load test
14. **Companion tools** — HarryAnalysis, HarryGraph, HarryCounter (can start earlier in parallel)

---

*Last updated: 2026-06-22*
*Authors: Customer + Claude Sonnet 4.6*

---

## 19. SOW Compliance — Open Items & Known Gaps

> Source documents: `MP2 Vision SOW 2025-11-05.pdf` + `M1X Inspection Addendum 2026-02-18.docx`
> Gap analysis performed 2026-06-22. Items marked CRITICAL must be resolved before FAT.
> Items marked PRE-SAT can be deferred but must be planned now.

---

### 19.1 CRITICAL — Must fix before FAT

#### 19.1.1 Collage file size limit (SOW §5.2.2) — ✅ DONE (2026-06-22)
**Requirement:** Each collage must not exceed **128 KB**.
**Implemented:** `CollageComposer` re-encodes JPEG at iteratively lower quality (start 85, step −5, min 30) until the output is ≤ the limit; a WARNING is logged if the minimum quality is reached and the file still exceeds the limit. The limit is configurable via `[Collage] MaxFileSizeKB` (default 128).
**File:** `HarryDataServer/Infrastructure/CollageComposer.cs`, `Services/CollageService.cs`

#### 19.1.2 MSA PDF reports (SOW §3.2.1) — ✅ DONE (2026-06-22)
**Requirement:** At the end of every MSA/LimitSample run, generate **2 PDF reports**:
- Report 1: All measurement results (name, expected, actual, pass/fail)
- Report 2: Only failed entries

**Implemented:** `PdfReportService` (QuestPDF, Community licence) generates both reports after every evaluation in `MsaService`. Files: `<Module>_<Type>_<DDMMYY_HHMMSS>_AllResults.pdf` and `_FailuresOnly.pdf`, written to `[MSA] ReportPath` (fallback `[MSA] ReferencePath\Reports`). Layout: header (module, type, run datetime, overall PASS/FAIL), table (Measurement | Expected | Actual | Cg/Cgk or %P/T | Pass/Fail), footer (generated-by + timestamp + page). The MSA tab has **Open All Results** / **Open Failures Only** buttons that open the PDF (generating on demand from the loaded run if it does not yet exist).
**File:** `HarryDataServer/Services/PdfReportService.cs`, `MsaService.cs`, `Controls/ucMsaControl.xaml`

#### 19.1.3 M1X LimitSample batch confirmation flow (Addendum §2.2)
**Requirement:** M1X LimitSample run is NOT terminated automatically after a fixed number of parts. After each set of 4 parts is measured, the **operator must confirm** whether more parts are coming. Only after "no more parts" confirmation does the run complete.
**Current state:** Not implemented — our LimitSample run ends after a fixed cycle count.
**Action:** SPS channel for M1X LimitSample needs a protocol extension: after each batch of 4, send a "ready for next batch / end run?" prompt back to PLC/operator. Clarify the exact SPS signal with Harry's (which channel, what message format). This may require a new SPS channel command type.
**File:** `HarryDataServer/Communication/TcpSpsServer.cs`, `SpsChannel.cs`

---

### 19.2 PRE-SAT — Must plan, can implement after FAT

#### 19.2.1 Shift counter with PLC reset signal (SOW §4.3)
**Requirement:** Three counter types per failure group:
1. **Shift Counter** — resets on PLC "Reset" signal at shift change
2. **Resettable Counter** — resets on operator demand (any time)
3. **Last-Shift Counter** — snapshot of previous shift's counts

**Current state:** HarryCounter tool counts NG in a date range but has no shift-reset concept.
**Action:** Add a new SPS command type (e.g. on Ch1 KeepAlive or a dedicated channel) for `RESET_SHIFT_COUNTER`. Store shift-reset timestamps in a new `shift_events` table. HarryCounter reads counts between consecutive reset events. Discuss exact PLC signal format with Harry's.

#### 19.2.2 Failure Warnings — X-in-a-row (SOW §4.4)
**Requirement:** PLC-tracked warning when X failures of the same inspection group occur in a row (or X of Y). Warning hierarchy: Nest > Application Station > Overall Module. Components: Lubra, Frame, Anodes, Blades, Trimmer. Stations: all M50 camera stations.
**Current state:** Not implemented.
**Action:** Implement a sliding-window failure counter per (component × station × nest) group in `PartExitOrchestrator`. Thresholds configurable in Harry.ini (`[Warnings] X_in_a_row`, `X_of_Y_window`). Send warning flag in SPS KeepAlive response when threshold crossed. This is complex — design separately with Harry's input on threshold values.

#### 19.2.3 Last NG image on dashboard (SOW §4.1)
**Requirement:** Dashboard must show images of the last NG parts.
**Current state:** Collage tab shows last 4 OK collages. No NG image viewer.
**Action:** Add an "NG Images" section to the Overview or Collage tab. On part exit with NG result, load the most recent full-resolution NG image path from the backup folder and display thumbnail (frozen BitmapImage, off-thread load, same pattern as collage thumbnails). Keep last 4 NG thumbnails in a `Queue<(string path, string serial, DateTime time)>(4)`.
**File:** `HarryDataServer/Controls/ucCollageControl.xaml`, `MainViewModel.cs`

---

### 19.3 CLARIFY WITH HARRY'S — Questions before/during commissioning

#### 19.3.1 M1X FTP connection (SOW §5.2.1)
**SOW note:** "New: Need connection between M1X vision to FTP server."
**Question:** M1X camera images are not delivered via the existing TCP telegram channel. Does Harry's expect us to run an FTP server that M1X uploads to? Or will M1X push images to a shared NAS path directly? What is the agreed folder structure for M1X images?
**Impact:** If we need to run an FTP server, this is significant new scope. Clarify before commissioning starts.

#### 19.3.2 LimitSample tolerance entries for measurement values (SOW §3.2.1)
**SOW description:** For measurement values (non-boolean), LimitSample entries can specify `[Expected value] ([Lower tolerance]; [Upper tolerance])` rather than just pass/fail.
**Current state:** HarryLimitSample works with ShouldPass / ShouldFail / Ignore (boolean only).
**Question:** Do any M50 or M1X measurements require numeric tolerance matching in LimitSample? If yes, HarryLimitSample needs a tolerance-entry mode and `MsaCalculator` needs a numeric comparison path.

#### 19.3.3 CSV datetime format (SOW §5.1.2) — ✅ DONE (2026-06-22)
**SOW requirement:** Datetime in filenames must be `DDMMYY_HHMMSS`.
**Implemented:** Centralised in `Infrastructure/FileNaming.cs` (`DateTimePattern = "ddMMyy_HHmmss"`). All generated filenames now use it: main/MSA/diagnostic CSV (`CsvFileWriter`), the MSA-tab CSV export, the log export, and the companion-tool CSV exports (`HarryShared.Data.CsvExport`). MSA CSV files are labelled module + type and stamped DDMMYY_HHMMSS. GSM run subfolders use the same stamp (see §1.2.1 constants in `FileNaming`).
**File:** `HarryDataServer/Infrastructure/{FileNaming,CsvFileWriter}.cs`

#### 19.3.4 HMI tolerance adjustment (SOW §4.1)
**SOW requirement:** All tolerances (limits) visible and adjustable on HMI, passcode-protected.
**Current state:** Limits come from Keyence Settings telegrams and are stored in the `settings` table. Our dashboard shows them read-only.
**Question:** Does Harry's expect limits to be writable FROM our WPF dashboard (and pushed back to the Keyence controller)? Or is the Keyence HMI the only place for limit adjustment, and our dashboard just displays them? This is a significant scope difference.

#### 19.3.5 M1X 4-parts-in-1-image filename (SOW §5.2.2)
**SOW requirement:** M1X captures 4 nests in one image. Filename must include all 4 parts' SZIDs.
**Current state:** Our image file-matching logic uses the 12-char SZID prefix per part. M1X images with 4 SZIDs in the filename won't match this pattern.
**Question:** What is the exact filename format Harry's will use for M1X multi-part images? Our `ImageHandler` needs a special matching rule for M1X images.

---

### 19.4 VERIFY DURING TESTING — Implementation checks

These items are implemented but need on-site verification:

| Check | What to verify | File |
|-------|---------------|------|
| Collage sources M2X + M50 only | M1X images must NOT appear in collage | `CollageComposer.cs` |
| GSM CSV folder name | Must be "Golden Sample Data" with subfolder TestType+DDMMYY_HHMMSS+Module (constants in `FileNaming`) | `Infrastructure/FileNaming.cs` |
| GSM images folder | Must be "Golden Sample Images" with run subfolder (constants in `FileNaming`) | `Infrastructure/FileNaming.cs` |
| Full-res retention configurable | Default 30 days via `[NAS] FullResRetentionDays`; per-type NG/Diag/GSM keys fall back to it | `ImageCleanupService.cs` |
| Backup folder YYYY\MM\DD structure | Year/month/day subfolders (no hour level) | `ImageHandler.cs` |
| Low-res delete after collage | For OK parts: individual BMP deleted after confirmed collage write | `ImageHandler.cs` |
| Low-res delete for NG | NG parts: low-res kept at part exit; deleted only when the matching full-res NG image is deleted (linked by 12-char serial prefix) | `ImageCleanupService.cs`, `PartExitOrchestrator.cs` |
| Humidity stored per part | m1x_humidity in dmcserial populated from telegram | `PartExitProcessor.cs` |

> **MSA cycle count is not configured by us.** The number of measurements per MSA run
> (≈50 for MSA1, 3 per part for MSA3, batch-driven for LimitSample) is controlled entirely
> by the SPS/PLC. We receive every measurement via TCP and aggregate by BaseID, so the
> evaluation works for any number of measurements — there is no cycle-count INI key.
