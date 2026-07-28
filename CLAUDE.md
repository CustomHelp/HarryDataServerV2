# CLAUDE.md — HarryDataServer V2
## Master Instruction File for Claude Code

> This file is the single source of truth for the entire project.
> Read it completely before writing any code, creating any file, or making any architectural decision.
> Update this file when specifications change.

> **STANDING RULE — Help stays current.** On every feature or UI change, update the in-app help
> content (**both German and English**, `HarryShared/Help/SuiteHelp.cs`) **in the same commit** as
> the change. The `?`-button / F1 help must always describe the current behaviour.

> **STANDING RULE — UI language is ENGLISH-only (since 2026-07-23).** All user-facing text in the
> server and every companion (buttons, labels, tooltips, context menus, status/log messages, dialogs,
> verdict/reason strings, PDF reports) is **English**. The `[General] Language` INI key is **kept for
> back-compat but ignored** — there is no runtime German UI. The **only** bilingual surface is the
> in-app `?`/F1 help (`HarryShared/Help/SuiteHelp.cs` + `HelpViewModel`), which stays **DE+EN** with a
> language toggle. New strings must be added in English; keep the help's DE+EN in sync (help rule above).

> **STANDING RULE — Production, builds & deploy (since 2026-07-23).** Production runs from
> **`F:\003_Deploy\HarryDataServer\App\`** (server: `App\HarryDataServer\HarryDataServer.exe`; each
> companion in its own `App\<Tool>\` sibling). **Building in the repo is always allowed** (Debug and
> Release) — it does not touch production. **Only `tools\deploy.cmd` needs announcement + explicit GO
> + a plant stop**, because it overwrites the live `App\` folder. **Never start a second instance
> against the live plant** — it would double-bind the PLC ports **6000–6006**, the Keyence camera
> connections and the MySQL writer (`SettData`). Companion tools and read-only DB access (`GetData`)
> are safe to run anytime. **`D:\` is the DVD drive — never write there;** use `F:\` for logs
> (`F:\004_Logs`), MSA fallback (`F:\003_Deploy\MSA_Reports_Fallback`) and deploy.

> **STANDING RULE — no plaintext passwords in new repo files (since 2026-07-28).** Credentials stay on
> the machine (`F:\002_Configs\Harry.ini`). A file newly added to the repository must **mask** them,
> like `config/live/Harry.ini` does: `Password=<siehe F:\002_Configs\Harry.ini>` plus a note in the
> accompanying README saying that this is the only line differing from the live file. This applies to
> INIs, scripts, docs, test fixtures and commit messages alike.
> **Open cleanup item (pre-existing, not yet done):** two older files still carry the DB password in
> clear text — **`HarryDataServer/Harry.ini` (line 16)** and **`tools/customer/Harry.customer.ini`**.
> Both predate this rule. Cleaning them up needs a decision (placeholder + a documented step in the
> deploy/customer-package procedure, since the customer INI is generated from the template), and the
> repository must stay **private** until then. The customer changes the passwords after deployment
> anyway (§8).

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
> receive path normalises Serial1 through `Infrastructure/SerialNumberHelper`** — it drops the
> controller's trailing `0` padding down to the meaningful length *only when the tail past that
> length is all `0`* (never a blind `TrimEnd('0')`, so a real serial that legitimately ends in `0`
> is preserved) and caps to 22.
>
> **Two serial KINDS, two lengths (confirmed 2026-07-27).** The FRAME serial (SZID, M1X/M5X) is
> **19** (`[General] SerialNumberLength`, `Normalize`); the TRIMMER serial (Virtual Serial, M20/M21)
> is a **shorter, separate format — 13** on the live line (`[General] TrimmerSerialNumberLength`,
> `NormalizeTrimmer`). They must not share one length: the camera padded a 13-char trimmer to 19
> (13 + 6 zeros) while the SPS delivered it at 13, so `measurements_serial_trimmer` and the part-exit
> serial never matched — the part-exit CSV lookup fell back to a prefix match (a WARNING **2656×** in
> the live logs) and trimmer image search missed. The trimmer length is applied at
> `MeasurementProcessor` (M2X camera write) and `SpsPartExitData.TryParse` (VirtualSerial); the frame
> length everywhere else. `Configure`/`ConfigureTrimmer` are set once at startup (`App.xaml.cs`).
> The **DMC / Serial2 is a separate, wider field and is NOT length-normalised**. **Serial2 (tokens
> 35–66)** keeps its full 32 chars. Image-filename search uses the 12/14-char prefix (frame/BaseID);
> **DE trimmer image deletion uses the FULL 13-char serial** as the Field 1 prefix (a 12-char key
> would spill onto the adjacent serial, which differs only in the 13th char).
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

**Triggers after receiving (`PartExitOrchestrator`):**
> **All image actions below search `01_Low_Resolution_Individual` ONLY** and share one implementation
> (`ImageHandler.ApplyAsync`) — see §11 "Folder roles + Delete Logic" for the binding target concept.

- **OK:** CSV export ‖ Collage (if `Collage_Generate`) ‖ image handling. The image action depends on
  **`Collage_Generate` only**: on → collage, then **delete** the originals; off → **move** them to
  `[NAS] BackupFolder\YYYY\MM\DD`. MSA parts are acked but never touch the production tables.
- **NG:** CSV export ‖ **low-res images deleted without a replacement** (changed 2026-07-28 — they used
  to be kept; the NG evidence is the full-res image in `03`, which is never touched by the server).
- **Unknown** (field 14 is neither `OK`/`NG`/`DE`): `dmcserial` row + image delete like NG, but
  **NO CSV row**, and always a WARNING with the raw field 14 and the raw telegram.
- **DE (deleted part):** a scrapped part. It is **not a finished part**, so the early DE branch in
  `HandleAsync` returns before `SaveDmcAsync`/`WritePartAsync` — **NOTHING is written to `dmcserial`
  and NOTHING to the production CSV**. It only **purges the part's low-res images**. DE is a full
  15-field telegram (`ResultStatus=DE`, `mode=Normal`) and is **polymorphic** on the live line (verified
  2026-07-27 over 95 DE part-exits): **91 carry the full frame SZID** (an assembled/partly-assembled
  part discarded) and **4 carry only the trimmer serial** (a loose rejected trimmer, M2X images). The
  server therefore deletes by **both** the frame SZID (19) **and** the trimmer serial (13) when present
  — matched as the **Serial1 prefix** (real filenames store Serial1 as the serial right-padded with `0`,
  **no** separator, so `StartsWith` is exact and never spills onto an adjacent serial), searched
  recursively in the low-res root (incl. legacy `YYYY\MM\DD` day-folders). **The NG (03) and diagnostic
  (04) roots were removed from the search on 2026-07-28.** The measurement rows are left untouched; the
  log line with count + keys (WARNING when 0 found) is the sole record of the removal. DE always parses
  correctly as `PartResult.Deleted` (no malformed telegrams in the live logs).
- **No frame serial:** no `dmcserial` row is written at all (it would collide with every other
  serial-less part on `uk_serial`) — a WARNING with the raw telegram is logged instead.
- **ACK:** `<SZID padded to 32>;true|false` + CR — unchanged. The orchestrator additionally returns its
  measured duration, which is appended **only** to the UI's "Last responses" line
  (`…;true (87 ms)`), never to the telegram.

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

**PUSH model (not poll) — changed 2026-07-21.** The PLC sends the `Request` **once**. The server
answers **`Wait;<BaseID>` immediately**, then — when the evaluation finishes — **pushes the result
on the SAME open connection without the PLC requesting again** (`ISpsServer.PushMsaResultAsync` →
`TcpSpsServer`). The requested BaseID is **mirrored back as field 1** so the PLC can correlate the
push with its request — format `<Status>;<BaseID>[;<description>]`:
- `Wait;<BaseID>` — immediate acknowledgement (evaluation running); **no further Wait polling needed**
- `OK;<BaseID>` — MSA passed (pushed when done)
- `NG;<BaseID>` — MSA failed (pushed when done)
- `Error;<BaseID>;<description>` — error (BaseID field empty when the request format was invalid,
  e.g. `Error;;expected 'Request;<BaseID>'`)

> **The PLC MUST keep the request connection open** to receive the pushed result (coordinate with
> the PLC programmer). If it closes the connection, the push is logged as skipped ("no open PLC
> connection"); the result is still cached, so a later re-`Request` on a fresh connection returns
> it (poll fallback retained). Because the PLC no longer re-polls, `MsaService.EvaluateAsync`
> **retries internally** (up to `MaxGatherAttempts`, one flush interval apart) while the run's rows
> are still being committed to `msa_measurements` — it never gives up waiting for a second request.
> Writes on a connection (receive-loop responses + unsolicited pushes) are serialised by a
> per-connection write lock.

---

## 6. Serial Number Concept

> Serial1 = tokens 3–34, Serial2 = tokens 35–66 (each 32 comma-tokens, see §4).

### Normal Mode
- **M10/M11:** SZID (frame identity) in Serial1 (tokens 3–34). Serial2 empty. Normalised to the
  **frame** length (`[General] SerialNumberLength`, **19**).
- **M20/M21:** Virtual Serial (trimmer identity) in Serial1 (tokens 3–34). Serial2 empty. Normalised
  to the **trimmer** length (`[General] TrimmerSerialNumberLength`, **13** — a shorter, separate
  format; see §4). This is the length used for `measurements_serial_trimmer`, the part-exit lookup
  and DE trimmer image deletion.
- **M50:** SZID in Serial1 (tokens 3–34). Serial2 empty.
- **Part Exit (Ch2):** All three known: DMC + SZID + VirtualSerial (SZID @19, VirtualSerial @13).

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

> **Corrected 2026-07-28 — there is NO underscore in the serial.** Field 1 of a real filename is
> the **bare serial right-padded with `0`**, no separator inside it
> (`2707261603210031811000-000…0-1-M50_ST140_KF1-1-&Cam1Img.bmp`). The V1 carry-over
> `CollageService.FormattedSerials`, which inserted a `_` after char 12, therefore made **every**
> `filename.Contains(serial)` test fail: no collage source image was ever found and no OK-part
> low-res image was ever cleaned up (live WARNINGs `no images found … for '270726160321_0031811,
> 260727000164_0'` while those files sat in `Z:\01…\Input`). Search keys now come from
> `SerialNumberHelper.ToImageSearchKey` (strips separators, caps to 22, **no** `TrimEnd('0')`), and
> every search site re-applies it immediately before searching.

Image filenames start with **Serial1** = the serial right-padded with `0` (no separator). The full
binding filename spec — all six fields, the field content per mode/module, the camera-number rule and
the two live deviations (M2X camera 1 swapped widths, M50_ST040 legacy underscore/OCR form) — is in
**§11 "Image Filename Format — BINDING SPEC"**. The one parser is `Infrastructure/ImageFileName.cs`.

Search keys, always matched **field-accurately** (never `Contains` over the whole filename — in MSA
mode Serial2 carries a real DMC that can contain any digit sequence):

| Purpose | Key | Matched against |
|---------|-----|-----------------|
| Part-exit image handling, DE deletion, collage source | frame SZID **19** / trimmer **13** (full length) | **prefix of Serial1** |
| NG ↔ low-res retention linkage | first **12** chars | **Serial1 prefix on both sides** (parsed, not raw filename) |
| MSA run-image collection | **14**-char BaseID | **prefix of Serial1** |
| MSA per-part lookup | DMC | **Serial2** |

**MSA images (`Serial2 ≠ zeros`) are excluded from every production sweep** and are never deleted by
production retention; an unparsable filename is likewise kept and reported (WARN).

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

### MSA Evaluation Methodology (implementation — `MsaService.Evaluate`, verified 2026-07-21)

**One study per measurement over all parts; loops = repetitions — NOT one MSA per part.**
`GatherAsync` pulls every `msa_measurements` row for the run (exact `base_id`, scoped by module
`controller_name LIKE '<Module>%'`, so a module run legitimately spans several cameras, e.g.
`M50_ST110_KF1` **and** `KF3`). Rows are grouped by `definition_id` → **one result per
(camera × measurement)**; the `Controller` is carried on every result and report row so the same
`display_name` on KF1 and KF3 is disambiguated (this is the "doubled rows" the un-labelled report
used to show — not two parts).

Per type (thresholds in `MsaCalculator`: Cg/Cgk ≥ 1.33, %P/T ≤ 20 %):
- **MSA1** — all values of the measurement are the 50 repetitions of one part; `σ` = sample
  StdDev; `Cg = 0.2·T/(6σ)`, `Cgk = (0.2·T − |x̄−xm|)/(6σ)`; `xm` from the reference file by
  `display_name`. `T = USL − LSL` from the latest `settings` limits for (camera, parameter set).
- **MSA3** — group the measurement's rows by DMC = parts, loops = repetitions;
  `%P/T = 6·√(ΣΣ(x̄ᵢ−xᵢⱼ)² / DoF)/T`, `DoF = Σ(nᵢ−1)` (dynamic — correct for any part/loop count).
  MSA3 uses **only** `T` + the values (the reference file is not needed). This is the
  repeatability (EV) vs. tolerance; the automated vision system has no appraiser term, so EV ≈ GRR.
- **LimitSample** — `shouldFail` from the reference; pass unless a prepared error was not rejected.

**Never a silent 0/FAIL (`MsaEvaluationText`).** Every FAIL/degenerate result carries a plain-text
`Reason` (also logged as WARNING): e.g. *no tolerance available (no Min/Max limits stored — request
a Settings telegram)*, *only n=3 value(s) (need ≥ 2)*, *%P/T 34.2 % > 20 %*, *Cgk 0.9 < 1.33*. The
**most common live cause of all-zero/FAIL is an empty `settings` table → `T = 0`**; the tolerance is
`Max − Min` from `settings`, which is populated only after a Settings telegram is received/requested.

**Overall verdict is tri-state — never a vacuous PASS (`MsaVerdict`, added 2026-07-21).** Only
measurements with `Evaluated = true` count toward the verdict (MSA1/3: a real capability with
`T > 0` and enough data; LimitSample: a reference entry existed AND the camera actually judged). The
run is **`Invalid`** (pushed as `Error;<reason>`, not OK) when: no measurements, or **0 evaluated**
(e.g. reference missing / all "no reference entry" / no limits), or — **LimitSample** — **no expected
error (prepared reject) was checked**. `Pass` requires ≥1 evaluated, all evaluated passed, and (for
LimitSample) ≥1 expected reject verified; otherwise `Fail`. Per-row `evaluated` is stored in
`msa_results`, so the UI (`MsaReportData.FromRun`) recomputes the same verdict and never shows a
vacuous PASS either.

**Camera-did-not-judge (task 4).** A controller that produced **no real OK/NOK** in the run (only
status 2 "not evaluated" / −1) is surfaced per controller in the report head and log
("*camera did not evaluate (only status 2/−1) — check program/mode*"); its LimitSample features are
neutralised (neither pass nor fail, `Evaluated = false`) so they cannot create a false PASS.

**LimitSample teach guard (`HarryLimitSample`).** The teach tool pre-marks each measurement from its
result status (1 → ShouldPass, 0 → ShouldFail, else Ignore). Saving is **refused** when the selected
module's controllers produced no judgement at all in the scanned part ("*Kamera hat nicht bewertet –
Einlernen nicht möglich*"), and a reference with **0 ShouldFail** entries is flagged (it would make
the run `Invalid`).

**Reference loading is always logged with the full resolved path** (`MsaReferenceLoader`):
`LOADED from <path> — N xm reference(s), M limit-sample entrie(s) (K expected reject(s))` or
`NOT FOUND at <path>`. The path resolves relative to the Harry.ini folder (absolute/UNC honoured).
The PDF head shows the reference file path + modified date (or NOT FOUND); the on-demand UI path
fills it from config so it is never a misleading "(none configured)".

**Report + raw export + paths (tasks B/C/D):**
- Each `msa_results` row also stores `n_values`, `mean_value`, `std_dev`, `reference_value`,
  `tolerance`, `criterion`, `reason` (schema auto-added on startup).
- The **PDF reports** (AllResults + FailuresOnly, landscape) show a head with msa_type, controllers,
  #parts (DMCs), #loops, time range, applied criterion, and the reference file (**full path +
  modified date, or NOT FOUND**); each row shows Controller, n, Mean, StdDev, Ref(xm), Tol(T),
  Cg/Cgk or %P/T, Result and Reason.
- A **raw-data export** for Minitab is written next to the PDFs: long format
  `Controller;BaseID;Loop;DMC;Measurement;Value;Status;Timestamp` (all cameras/parts/loops/
  measurements). Per CLAUDE.md §15 **no Excel library is used** — it is a `;`-separated UTF-8-BOM CSV
  that opens in Excel and imports 1:1 into Minitab.
- **Report layout (`[MSA] ReportPath`, changed 2026-07-21):** one folder **per run** —
  `<ReportPath>\<yyyy-MM-dd>\<Module>\<BaseID>\` with three subfolders **`PDF\`** (the two reports),
  **`RAW\`** (the Minitab CSV) and **`IMG\`** (the run's images, see below). Date = run's calendar day.
  Absolute local, mapped-drive and UNC paths are supported; relative resolves against the config dir.
  If the primary path is unreachable at write time it falls back to **`[MSA] ReportFallbackPath`**
  (default `D:\HarryDataServer\MSA_Reports`) — SAME layout — with a WARNING, never a crash/data loss.
  Old flat `<ReportPath>\<Module>\<Date>\` files are left untouched (no migration). The per-run
  **measurement summary CSV** still lives under `[MSA] ResultPath\YYYY\MM\DD\<BaseID>\CSV`.
- **Run images (`IMG\`) — MOVED since 2026-07-28:** `MsaService.MoveRunImages` → `MsaRunImages.Move`
  **moves** (no longer copies) every image whose filename **Serial1** starts with the 14-char BaseID out
  of the **GoldenSample transit folder** (`[NAS] HighResGoldenSamplePath`) into `IMG\`. Crossing the
  drive boundary (Z: → X:) it is copy → size-verify → delete. A single failed move is a WARNING and the
  **original is left in place** (the 3-day retention removes it later); the run is never failed by an
  image. Missing/unmatched images are not a run error — only a log line "n found / n moved".
  Rationale: `05` is a transit buffer, so a finished run takes its images with it (copying left every
  run's originals there forever).
- **No MSA summary CSV any more (removed 2026-07-28):** `[MSA] ReportPath` is the single file-based MSA
  location (PDF + RAW + IMG); the numbers additionally live in `msa_results`. `[CSV] CSVMSA_Save` is
  deprecated/ignored (WARNING when set); `[CSV] CSV_MSAPath` is only still read as the retention root
  that ages out the existing legacy `Y:\01` tree. Existing files there are left untouched.

### MSA Reference Files

**MSA1 xm — per reference part with BEST-MATCH (changed 2026-07-21, task C).** The milled MSA1
reference parts have no readable DMC (the camera emits fake DMCs, and one physical part appears under
several fake DMCs in a run), so a part is matched to a reference by its measured values, not by DMC.
Files: `<ReferencePath>\<Module>\MSA1\<Name>.json` (`HarryShared.Data.Msa1Reference`):
```json
{ "module":"M50", "label":"Referenzteil A", "created_at":"…", "template": false,
  "values": { "Anode_Flatness_L": 0.012, "Pin_Area_1": 3.20 } }
```
- **DEMO templates:** at startup a `DEMO_<Module>.json` is auto-created for M10/M11/M20/M21/M50 with
  ALL Result measurement names (from `measurement_definitions`) and 0 values + `template:true`.
  Templates (`DEMO_` name or `template:true`) are **ignored** during evaluation — copy, rename, fill in.
- **Best-match (`Msa1Matcher`, f=0.10, plausible ≥ 50 % hits):** per part (fake DMC), a measurement is
  a *hit* when `|mean − xm| ≤ 0.10·tolerance`; the reference with the most hits wins (tie → smallest
  Σ|mean−xm|/tolerance). Plausible match → Cg **and** Cgk (xm from the reference); a measurement
  missing from the matched reference → **n/a** (never a fail). No plausible match → **Cg-only + note**.
  Ambiguous (two references ~equally good) → a warning. Several fake DMCs may match the same file.
  The chosen reference (label + file) and score are shown per part in the PDF and UI and stored in
  `msa_results` (`matched_reference`, `match_score`). Legacy `MSA_<Module>.json` `references` is a
  fallback when no MSA1 files exist.

**MSA1 xm — legacy module-wide** in `<ReferencePath>\MSA_<Module>.json` (`references`: measurement → xm),
read only as the best-match fallback above.

**LimitSample — one file PER PART (per DMC), changed 2026-07-21.** Path:
`<ReferencePath>\<Module>\LimitSamples\<sanitized-DMC>.json` (`[MSA] ReferencePath`, resolved from
Harry.ini by BOTH the server and the HarryLimitSample editor). Schema (`HarryShared.Data.LimitSampleReference`):
```json
{
  "dmc": "26062612255644444444000000124051",
  "module": "M50",
  "taught_at": "2026-07-21T17:04:33",
  "source_base_id": "50260721170000",
  "controllers": ["M50_ST110_KF1", "M50_ST110_KF3"],
  "expected": { "Anode_Flatness_L": "ShouldFail", "Pin_Area_1": "ShouldPass" }
}
```
Only measurements the camera actually judged (status 0/1) are stored (status-2 omitted). The DMC is
sanitized for the file name; the original DMC stays in the JSON.

**Per-part evaluation:** each run DMC is checked against ITS file — every `ShouldFail` (prepared
error) must be rejected for that part, every `ShouldPass` accepted. A run DMC with no file → that
part **INVALID** with a plain reason; a taught DMC missing from the run → a **note** in the report.
Overall PASS only if ≥1 prepared error was checked and all evaluated parts passed. **Back-compat:**
when a module has no per-part files, the legacy module-wide `limit_sample_expected` in
`MSA_<Module>.json` is read as a fallback with a WARNING ("old format"); the editor writes only
per-part files. The editor lists taught parts (DMC, taught-at, #prepared errors) with open/edit/delete
and logs + shows the fully resolved save path.

**Per-part verdicts + SPS aggregation (task A, 2026-07-21).** LimitSample/MSA1 compute an explicit
verdict PER PART (`MsaEvaluationText.PartVerdict`): INVALID if a part has no reference / nothing
evaluated / (LimitSample) no prepared error checked; FAIL if any evaluated measurement failed; else
PASS. The run result reported to the PLC is the **worst of the parts** (`OverallFromParts`): any part
INVALID → `Error;<reason>`; else any part FAIL → `NG`; else `OK`. MSA3 is one study over all parts
(`OverallVerdict`).

**MSA UI (task B).** The MSA tab shows, per selected run: a **parts list** (DMC · per-part verdict ·
"x/y ok" · MSA1 matched reference) and, for the selected part, its **measurements** (ok / nicht ok /
n.a. + reason). Buttons act on the selected part: **Open PDF Complete**, **Open PDF (nur Fehler)** and
**Open Folder** (`<ReportPath>\<Date>\<Module>\<BaseID>`). Per-part PDFs are generated for
LimitSample/MSA1 with **BaseID + DMC** in the file name
(`<Module>_<Type>_<BaseID>_<DMC>_AllResults.pdf` / `_FailuresOnly.pdf`); MSA3 keeps the run-level
report. The Excel-export button was removed (the raw CSV already lives in the run's `RAW\` folder).

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

### Where the templates live — two copies, one truth (clarified 2026-07-28)

| Copy | Role |
|------|------|
| **`F:\002_Configs\Templates\*.json`** | **THE ACTIVE templates.** `[CameraN] JsonParameters=Templates\…` is relative and is resolved against the folder that holds Harry.ini (§10), i.e. normally this one. **Customer-owned** — the deploy never writes here. |
| **`HarryDataServer/Resources/Templates/*.json`** (repo) | **Reference + loader fallback.** The `.csproj` ships them as `Content` into `<exe>\Templates\`. Synced to the live state on 2026-07-28 (see below). |
| `config/live/Templates/*.json` (repo) | Pure documentation snapshot of the live folder (§10), never read at runtime. |

**When the fallback actually applies** (`Configuration/JsonTemplateLoader.ResolveTemplatePath`) — the
resolution is **per file**: ① the configured path if `File.Exists`, ② else `<exe>\Templates\<name>`,
③ else `<exe>\<name>`, ④ else `null` (the camera gets no definitions). So ② is reached whenever the
configured path is missing, concretely:
- Harry.ini was found somewhere **other than** `F:\002_Configs` (search order ③/④ in §10: next to the
  exe, or legacy `D:\HarryDataServer`) — the relative `Templates\…` then points at that folder;
- `F:\002_Configs\Templates\` is unreachable (drive/share/permission) or a single file was
  renamed/deleted;
- a dev/test run on a machine that has no `F:\002_Configs`.

That is a **real runtime path**, which is why shipping stubs there was a latent defect: until
2026-07-28 the repo copy held `"STUB - Please fill telegram_place …"` placeholders for the four M1X
cameras (26 lines instead of 165), so a fallback would have registered TODO definitions. All 28 files
are now byte-identical to the live folder, including the 2026-07-21 correction that starts
`telegram_place` at **72**.

> **Standing rule — the deploy is not a template distribution channel.**
> `tools\deploy.cmd` mirrors the **build output** into `App\<Project>\` (`robocopy … /MIR /XD Logs
> Capture`), so it **does** refresh the fallback copy `App\HarryDataServer\Templates\` — that is
> intended, it is part of the binary. It **never** touches `F:\002_Configs\Templates\`: the live,
> customer-owned templates are outside the deploy scope (deploy.cmd header: "the live Harry.ini is NOT
> copied or changed here"). Changing a camera program therefore means editing
> `F:\002_Configs\Templates\` on the machine, and afterwards syncing the repo copies (reference +
> `config/live` snapshot) by hand — never the other way round.

---

## 10. INI Configuration (Harry.ini)

> **Config location:** All configuration lives in the central folder `F:\002_Configs`
> (Harry.ini, the `Templates\` subfolder with the JSON files, and later Collage.ini /
> MSA references). The application looks for Harry.ini in this order:
> `HARRY_CONFIG_DIR` env var → `F:\002_Configs` → next to the executable →
> legacy `D:\HarryDataServer`.
> **Relative paths** in Harry.ini (e.g. `Templates\Result_*.json`) are resolved
> against the directory that contains Harry.ini, so the whole config folder is portable.
>
> **Versioned snapshot: `config/live/` (added 2026-07-28).** `F:\002_Configs` lives only on the
> server drive, so the repo now carries a copy of the **real** live configuration — `Harry.ini` +
> the 28 `Templates\*.json` — under `config/live/` (see its README). It is **documentation/backup
> only, never read at runtime**, and must be re-synced by hand after a config change:
> `robocopy F:\002_Configs config\live\ Harry.ini` (+ the `Templates` subfolder).
> Do **not** confuse it with `HarryDataServer/Resources/Templates/*.json`, which the `.csproj`
> copies next to the EXE as the loader's fallback and which still holds **stubs** for the M1X
> cameras — those are customer-owned and deliberately left alone.
> `F:\002_Configs\MSA_References\` is **not** in the snapshot (it changes during operation through
> LimitSample teaching).

```ini
[General]
LogFilePath=D:\HarryDataServer\Logs\
LoggingActive=true
Language=English
SerialNumberLength=19          ; meaningful (unpadded) FRAME serial length (SZID, M1X/M5X)
TrimmerSerialNumberLength=13   ; meaningful (unpadded) TRIMMER serial length (Virtual Serial, M20/M21)

[MySQL]
Server=localhost
Database=camera_data
User=SettData
Password=1234Set
; NOTE: retention moved to the central [Retention] section. The old RetentionPeriodDays is still
; read as a DEPRECATED fallback (WARNING) when [Retention] Database_Production is absent.

[CSV]
CSV_BasePath=Y:\02_CSV_Merge      ; production CSV → CSV_BasePath\YYYY\MM\DD
CSV_DiagnosticPath=Y:\03_CSV_Diagnostic  ; diagnostic CSV → \YYYY\MM\DD
DataSetsPerFile=10000
CSV_Save=true
CSVDiagnostic_Save=true
; CSVMSA_Save removed 2026-07-28 (deprecated + ignored: the MSA summary CSV export is gone).
; CSV_MSAPath is only still read as the retention root of the existing legacy Y:\01 tree.

[NAS]
; Z:\ IS the images share, so the folders live directly under it (01_… … 06_…), NOT under Z:\Images.
; NAS sorting is OFF — no new YYYY\MM\DD day-folders in 01–05; existing ones are legacy stock.
; Folder roles: 01 = the ONLY folder part-exit image actions search · 02 = collage output ·
; 03 NG + 04 diagnostic = camera only, the program NEVER touches them · 05 = MSA TRANSIT buffer
; (a finished run MOVES its images to [MSA] ReportPath) · 06 = OK backup when no collage is made.
LowResIndividualPath=Z:\01_Low_Resolution_Individual\Input
CollagePath=Z:\02_Low_Resolution_Collage\Input
HighResNGPath=Z:\03_High_Resolution_NG\Input
HighResDiagnosticPath=Z:\04_High_Resolution_Diagnostic\Input
HighResGoldenSamplePath=Z:\05_High_Resolution_GoldenSample\Input
; OK part: the image action follows [Collage] Collage_Generate ONLY —
;   true  -> collage, then DELETE the originals
;   false -> MOVE the originals to BackupFolder\YYYY\MM\DD (copy, verify size, delete)
; NG / Unknown / DE: low-res images are DELETED without a replacement (NG evidence = full-res in 03).
; [NAS] DeletePictures was REMOVED 2026-07-28 (deprecated + ignored; a still-present key logs a WARNING).
BackupFolder=Z:\06_Backup

[Retention]
; Central retention policy (§11a). Every value is DAYS; 0 = NEVER delete. One RetentionService
; applies all of these at startup + every 24 h; master data (settings/definitions/cameras) is
; never touched. Legacy [MySQL]/[NAS] retention keys are read as a deprecated fallback (WARNING).
Images_NG=30
Images_Diagnostic=30
Images_GoldenSample=3     ; 3, NOT 30: 05 is only a TRANSIT buffer (finished runs are moved to X:)
Images_Collage=30
Images_Backup=30
Images_InputLeftovers=3   ; files stuck in a …\Input folder past N days = failed-run leftovers → delete + WARNING
                          ; MSA images are spared in 01–04 but NOT in 05 (transit buffer);
                          ; an unparsable filename is never deleted anywhere (reported instead)
Database_Production=35    ; measurements_serial(_trimmer) DROP PARTITION + dmcserial batch DELETE
Database_MSA=0            ; msa_measurements, msa_results → NEVER by default (QS data)
Reports_MSA=0             ; [MSA] ReportPath dated folders → NEVER by default (QS evidence)
CSV_Evaluation=365
CSV_Merge=365
CSV_ExtraResults=90

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

### Image Filename Format — BINDING SPEC (Philipp, 2026-07-28)

The Keyence controller writes the filename. The **same** camera program runs in Normal and MSA
mode, so the structure is identical — only the two serial fields differ. Field separator is the
**hyphen `-`**; the controller name and the image variable contain **underscores**, so `_` can
never be the separator.

```
{Serial1}-{Serial2}-{Overall}-{Controller}-{CameraNumber}-{ImageVariable}.bmp
```

| Field | Width / values | Content |
|-------|----------------|---------|
| **Serial1** | exactly **22** chars, right-padded with `0` | see mode table below |
| **Serial2** | exactly **32** chars, right-padded with `0` | see mode table below |
| **Overall** | `0` / `1` | camera's overall result |
| **Controller** | e.g. `M50_ST140_KF1` | **contains `_`** |
| **CameraNumber** | `1`–`4` on M1X · `1`–`2` elsewhere · always `1` on M50 ST110 | camera under the controller (this is the *camera number*, NOT a nest number — the older "Nest" naming was wrong) |
| **ImageVariable** | e.g. `&Cam1Img`, `&Cam1Img_Dark` | contains `&`, `_` and dots |

| Mode / module | Serial1 | Serial2 |
|---------------|---------|---------|
| Normal — M1X + M50 | frame serial (19 digits) + `0` padding | all zeros |
| Normal — M2X | **virtual (trimmer) serial** + `0` padding | all zeros |
| **MSA / LimitSample** | full **BaseID incl. loop counter** + padding | **the DMC lasered on the part** — real content, *not* a zero field |

> **`Serial2 ≠ all zeros` is the one reliable MSA marker** (`ImageFileName.IsMsa`). Every production
> sweep (DE purge, OK backup/delete, `[Retention] Images_InputLeftovers`, NG↔low-res linkage, collage
> source search) **skips MSA images** — they are QS evidence and are only ever removed by an explicit
> MSA retention key. A filename that cannot be parsed is likewise never deleted, only reported (WARN).

**Two deviations that really occur on the line (verified 2026-07-28 over 26 093 files, 100 % parsed):**

| Form | Who | What |
|------|-----|------|
| `SwappedWidths` | **M20/M21 camera 1** (~2 400 files/day) | the two serial field **widths are swapped**: Serial1 = 32, Serial2 = 22. Content unchanged (trimmer serial in Serial1, zeros in Serial2). → **open point for the camera side** |
| `LegacyUnderscore` | **M50_ST040_KF1**, OCR images only (~500/day, NG folder) | the **old V1 layout**: `_` separators, an extra `OCR` marker, and the serial split by a `_` after char 12 — `270726161219_00320440000000000000_1_M50_ST040_KF1_2_OCR_&Cam2Img_Dark.png`. The parser reassembles Serial1 **without** the `_`, so it matches with the same key as the modern form. → **open point for the camera side** |

> **Underscore history:** the very first V1 camera program wrote the serial with a `_` after char 12
> and used `_` as the field separator. Since the change to the two serial fields there is no
> underscore in the serial — the stale "`_` after char 12" documentation (corrected 2026-07-28) came
> from that era, and one un-migrated camera program still produces it (see `LegacyUnderscore`).

> **Parser: `Infrastructure/ImageFileName.cs` is the SINGLE place that splits a filename** — no other
> code may `Split('-')` an image name. Serial1/Serial2 are separated at the **fixed offset** (`-`
> expected at index 22, or 32 for the known swap), and the three trailing fields are read from the
> **end**, so a hyphen inside a DMC cannot shift any field. Unparsable → `null` + a WARN from the
> caller (one line per sweep with a count and one example), never a silent skip.
>
> **Matching is field-accurate (behaviour change 2026-07-28, was `Contains` over the whole name):**
> production keys (frame 19 / trimmer 13 / virtual serial, normalised via
> `SerialNumberHelper.ToImageSearchKey`) match **as a prefix of Serial1**; MSA searches match the
> **BaseID against Serial1** and the **DMC against Serial2**. The 12-char prefix is still used for the
> NG ↔ low-res retention linkage — but taken from the parsed **Serial1**, not from the raw filename.

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

### Folder roles + Delete Logic (TARGET CONCEPT, Philipp 2026-07-28 — binding)

| Folder | Role |
|--------|------|
| `01_Low_Resolution_Individual\Input` | camera writes the per-part low-res images. **THE ONLY folder any part-exit image action searches** (OK/NG/DE/Unknown) |
| `02_Low_Resolution_Collage\Input` | collage output (when `Collage_Generate=true`) |
| `03_High_Resolution_NG` | camera writes NG full-res images. **The program NEVER touches 03** — only retention (`Images_NG`, day-folders) |
| `04_High_Resolution_Diagnostic` | third threshold on OK parts (extra full-res image). **The program NEVER touches 04** — only retention |
| `05_High_Resolution_GoldenSample\Input` | first threshold (GoldenSample/MSA). **TRANSIT BUFFER:** an MSA completion **MOVES** the run's images to `[MSA] ReportPath`; whatever is left (aborted runs **and** the M1X production `.png` — deliberate decision) is deleted after **3 days** |
| `06_Backup` | target for an OK part's low-res images **only when no collage is generated**; retention 30 days |

> **NAS sorting is OFF (2026-07-28):** no new `YYYY\MM\DD` day-folders appear in 01–05; existing ones
> are legacy stock. Searches still run **recursively from the folder root** (`ImageFileName.SortedRoot`)
> and therefore tolerate them.

- **ONE image machinery for every part state** (`ImageHandler.ApplyAsync`, called via
  `PartExitOrchestrator.RunImagesAsync`): same root (`01`, recursive), **no extension filter** (`*.bmp`
  and `*.png` alike), field-accurate **Serial1-prefix** match, and **MSA images
  (`Serial2 ≠ zeros`) plus unparsable names are never touched**. Every delete/move logs count + keys;
  0 matches is a WARNING carrying the raw AND normalised key, the folder, the inspected count and the
  MSA-skip count.
- **OK images (part exit):** the action depends on **`[Collage] Collage_Generate` ONLY** —
  `true` → build the collage into `[Collage] Collage_ResultImages`, then **delete** the originals;
  `false` → **move** them to `[NAS] BackupFolder\YYYY\MM\DD` (copy → size-verify → delete).
  **`[NAS] DeletePictures` is deprecated and IGNORED** (a still-present key logs a WARNING at startup).
- **NG images (part exit, new 2026-07-28):** the low-res images are **deleted without a replacement** —
  the NG evidence is the full-res image in `03`, which is not touched.
- **Unknown result (field 14 neither OK/NG/DE):** handled like NG (`dmcserial` + image delete) but
  **without a CSV row**, and always reported as a WARNING with the raw field + raw telegram.
- **DE image deletion (ST160):** deleted by the full frame **SZID (19)** and/or the **trimmer serial
  (13)** — whichever the telegram carries — **in `01` only** (03/04 were removed from the search roots
  on 2026-07-28). No `dmcserial`/CSV write.
- **No frame serial → no `dmcserial` row** (it would collide with every other serial-less part on
  `uk_serial`); reported as a WARNING with the raw telegram instead.
- **NG low-res linkage (retention):** when an NG day-folder expires, the matching low-res images
  (12-char **Serial1** prefix on both sides) are deleted with it. **Kept as a FALLBACK** even though NG
  now deletes at part exit — a missed part exit (restart, lost telegram) or an image written after the
  part exit would otherwise orphan a low-res image that no other sweep picks up.
- **Image search key:** frame SZID (19) / trimmer serial (13) as a **Serial1 prefix** for every
  part-exit action; the first 12 chars of Serial1 for the NG↔low-res retention linkage; the 14-char
  BaseID for MSA run images.

### 11a. Central Retention (`RetentionService`, [Retention] section)

**One service ages out EVERYTHING** (replaces the old image-only `ImageCleanupService`). Runs ~1 min
after startup and then every 24 h. Every target has its own age in **days** in the `[Retention]`
section; **`0` = never delete**. Nothing is silent — each target logs an INFO line (n deleted /
nothing to do / disabled) and every path/access error is a WARNING with the path.

| Target | `[Retention]` key | What / how |
|--------|-------------------|------------|
| NG (+linked low-res), Diagnostic, GoldenSample, Collage, Backup | `Images_*` | delete whole `YYYY\MM\DD` day-folders older than N |
| `…\Input` leftovers | `Images_InputLeftovers` (default 3) | files left in an `\Input` folder past N days = failed-run remnants → delete + **WARNING**. **In 01–04 never deleted:** MSA images (`Serial2 ≠ zeros`) — kept and reported; the low-res Input additionally keeps NG-flagged files. **In 05 (MSA transit buffer) MSA images ARE deleted** (`isMsaTransitFolder: true`, 2026-07-28) — a finished run has moved its images to `[MSA] ReportPath`, so what is left is an aborted run or an M1X production image. **Everywhere: a name that does not parse is never deleted** (`RetentionService.ClassifyLeftover`) |
| MSA reports | `Reports_MSA` (**default 0 = never**) | dated folders under `[MSA] ReportPath` |
| CSV Merge / Evaluation / ExtraResults | `CSV_Merge` / `CSV_Evaluation` / `CSV_ExtraResults` | `YYYY\MM\DD` day-folders |
| Production DB | `Database_Production` | `measurements_serial(_trimmer)` via **DROP PARTITION** (standing rule — never DELETE on partitioned tables); `dmcserial` via bounded **batch DELETE** (`LIMIT 10000`, short pauses → no long locks) |
| MSA DB | `Database_MSA` (**default 0 = never**) | `msa_measurements`, `msa_results` via batch DELETE |

**Master data (`settings`, `setting_definitions`, `measurement_definitions`, `cameras`) is NEVER
touched.** `Database_MSA` and `Reports_MSA` default to **0 (never)** on purpose — they are QS data;
only the customer/QS enables ageing. **Legacy keys** (`[MySQL] RetentionPeriodDays`, `[NAS]
Retention*Days` / `BackupRetentionDays` / `FullResRetentionDays`) are still read as a **deprecated
fallback** with a WARNING when the matching `[Retention]` key is absent; the live INI is migrated so
no fallback fires there.

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
- Header: **2 rows**, dynamic from `measurement_definitions` — row 1 = merge group / controller,
  row 2 = variable name (see the merged-column section below)
- Missing values (camera was offline): empty column
- File rotation: on order name change OR when `DataSetsPerFile` rows reached
- Filename: `<DDMMYY_HHMMSS>_<OrderName>.csv` (SOW §5.1.2 stamp via `FileNaming.Stamp`; `NoOrder` when
  the order name is empty). Corrected 2026-07-28 — the old `YYYY-MM-DD-HH-mm-OrderName.csv` in this
  spec never matched the code.
- **Only OK and NG parts produce a row.** DE and an unrecognised result (`Unknown`) are excluded, so
  the production CSV contains finished parts with an understood result only.
- Path: `CSV_BasePath\YYYY\MM\DD\`

#### Merged columns: parallel strands + redundant control windows (changed 2026-07-28)

> **NOTE FOR QS / THE CUSTOMER — the column layout changed on 2026-07-28 (v2.0.0).**
> Files written **before** that keep the old 722-column layout; files written **after** it have
> **431** columns. The layout is fixed when a file is created and is **never changed inside an
> existing file** (`CsvFileWriter.Configure` runs on rotation only), so no file ever mixes both.
> **No value is lost** — the removed columns were structurally always empty.

The two production strands are mutually exclusive per part (strand A = M10 + M20, strand B = M11 +
M21) and the two M50 ST110 control windows inspect the same part, while their variable names are
**identical**. One column per controller therefore produced 292 column pairs of which exactly one half
was always empty. Those pairs now share a column (`Infrastructure/CsvColumnLayout.cs`):

| Merge group (header row 1) | Source controllers |
|---------------------------|--------------------|
| `M1x_<Station>_<KF>` | `M10_<Station>_<KF>` + `M11_<Station>_<KF>` |
| `M2x_<Station>_<KF>` | `M20_<Station>_<KF>` + `M21_<Station>_<KF>` |
| `M50_ST110` | `M50_ST110_KF1` + `M50_ST110_KF3` |
| unchanged | `M50_ST040_KF1`, `M50_ST120_KF1`, `M50_ST130_KF1`, `M50_ST140_KF1`, … |

- **Column count:** 706 active definitions → **414** measurement columns (292 of them shared);
  with the meta columns **722 → 431** total.
- **Value rule** (`Infrastructure/CsvMergeFill.cs`): the shared cell takes the **non-empty** value of
  its sources. Should both ever be filled, the controller **matching the part** wins
  (`M1xModule`/`M2xModule` from the part-exit telegram) and **one WARNING per part** is logged — never
  one per cell. The ST110 windows have no counterpart in the telegram, so there the first non-empty
  value wins and the new `M50St110Kf` meta column records which window it was.
- **Nothing is folded away silently:** a variable that exists on only ONE side of a pair keeps **its
  own column under the original controller name** and produces a one-off WARNING at header-build time.
  Today there is none — all five pairs are exactly identical in the live DB (0 one-sided variables).
- **New meta column `M50St110Kf`** (`1`/`3`, empty without an ST110 measurement) sits directly behind
  `M50Nest`; every other meta column keeps its name and relative order, and the delimiter/format are
  unchanged. Meta columns: **16 → 17**.
- **Validated against reality** before the change: all five pairs identical in
  `measurement_definitions`, and the live file `280726_141335_1118.csv` (4 099 data rows) pushed row by
  row through the new mapping — **1 477 452 filled cells before and after, 0 collisions, 0 data loss**.

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
- **Console tail auto-scroll (`Controls/TailScrollView.cs`, 2026-07-28):** ONE reusable wrapper provides
  the whole mechanic for the **log tab** and both **PLC channel lists** — wrap the list, nothing else:
  `<ctl:TailScrollView><ListBox …/></ctl:TailScrollView>`. At the bottom it follows new entries;
  scrolling up **or clicking a line** pauses following and holds the position **exactly** (anchored on
  the line's text, so a ring buffer dropping old rows cannot shift the view); while paused a
  **"▼ n new"** overlay counts the entries added since the pause and jumps back on click; scrolling
  back to the bottom (2-unit tolerance) resumes; nothing scrolls while the mouse button is held or a
  wrapped text control has a selection. The **selected line survives the per-tick rebuild**, so
  *Copy line* (context menu / Ctrl+C) works while the log runs.
  **Content changes are detected via `CollectionChanged`, not `ScrollChanged`** — with a full ring
  buffer the item count is constant, so `ExtentHeightChange` stays 0 and a scroll-event-based
  detection silently drifts and never counts. Pure view mechanics: no view-model involvement; the only
  requirement on an item type is a meaningful `ToString()` (the line text) as its stable identity.
- **PLC channel lists are in console order** (oldest at the top, newest at the bottom, changed
  2026-07-28 from newest-on-top) so they use the same mechanic as the log
  (`SpsChannelViewModel.Sync`).
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

### Deploy (production on the line)
Production runs from **`F:\003_Deploy\HarryDataServer\App\`**, populated by **`tools\deploy.cmd`**
(needs announcement + GO + a plant stop — see the standing rule at the top). Layout: one folder per
project — `App\HarryDataServer\`, `App\HarryAnalysis\`, … `App\HarryPareto\`. The previous deploy is
kept in `App_prev\` for rollback; `App\version.txt` records date + git hash.

1. Stop the server and all companions (nothing may hold the PLC ports / DB).
2. Build the solution in Release (`dotnet build -c Release`).
3. Run `tools\deploy.cmd` (snapshots App→App_prev, robocopies each project's `bin\Release\net8.0-windows\`).
4. Point the desktop shortcut at `App\HarryDataServer\HarryDataServer.exe` and start from there.
5. Config stays in `F:\002_Configs` (`Harry.ini`, `Collage.ini`, `Templates\`); `HARRY_CONFIG_DIR`
   can override the folder. The Tools tab finds companions under the sibling `App\<Tool>\` folder.
6. The full step-by-step stop-window procedure is in **`tools\DEPLOY_FENSTER.md`**.

### Deploy (customer companion tools — off-line, off the line)
`tools\package_customer.cmd` builds a **framework-dependent** ZIP per companion into
`F:\100_Installer\CompanionTools\` with a stripped customer `Harry.ini` (read-only DB placeholder,
network paths only, **no `F:` and no write user**) and a README. The target PC needs the **.NET 8
Desktop Runtime (x64)** — put its installer next to the ZIPs (customer installs it once first).
Restore runs once with a hard timeout (offline-safe; aborts clearly instead of retrying nuget.org),
then publishes use `--no-restore`. See the script header for details.

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
**Update 2026-07-28:** the live M1X images (`M10/M11_ST030|ST060_KF1`) carry **one** SZID and camera number **4** — no 4-in-1 filename appeared yet.

#### 19.3.6 Camera-side filename deviations (found 2026-07-28 on the live NAS)
Both are parsed by `ImageFileName` (nothing is lost), but both deviate from the spec Philipp confirmed
and should be aligned in the camera programs:
1. **M20/M21 camera 1** writes the two serial field **widths swapped** — `Serial1 = 32`, `Serial2 = 22`
   (~2 400 files/day). Camera 2 of the same controllers writes the spec form 22/32.
2. **M50_ST040_KF1** still writes its **OCR images in the old V1 layout** (~500/day into the NG folder):
   `_` separators, an extra `OCR` field, and the serial split by a `_` after char 12
   (`270726161219_00320440000000000000_1_M50_ST040_KF1_2_OCR_&Cam2Img_Dark.png`).

#### 19.3.7 Where do the M1X images belong? (found 2026-07-28 — **decided, see the note at the end**)
`Z:\05_High_Resolution_GoldenSample\Input` — the folder the server treats as the **MSA/GoldenSample**
source (`[NAS] HighResGoldenSamplePath`, read by `MsaService.CopyRunImages`) — currently holds ~1 000
**normal-production M1X images** (`M10/M11_ST060_KF1`, camera 4, `Serial2 = zeros`). Consequences today:
- No production flow consumes them: the **DE purge searches LowRes / NG / Diagnostic only**, so a
  scrapped part's M1X images are **not** deleted; the OK part-exit cleanup only searches the low-res path.
- The only sweep that ever removes them is `[Retention] Images_InputLeftovers=3` — i.e. they are deleted
  as "failed-run leftovers" after 3 days, with a WARNING. Nothing in the folder is older than 3 days yet.
**DECIDED 2026-07-28 (Philipp):** `05` is a **transit buffer** — a finished MSA run moves its images
out, and **everything left over, explicitly including the M1X production `.png`, is deleted by the
3-day retention**. The DE purge deliberately does **not** search `05`, so a scrapped part's M1X images
simply age out there. Consequence to be aware of: M1X **OK** images are therefore never moved into
`06_Backup` either. If M1X images should be backed up / DE-purged like the other modules, the camera
must write them into `01_Low_Resolution_Individual\Input` — that remains a camera-side question.

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
